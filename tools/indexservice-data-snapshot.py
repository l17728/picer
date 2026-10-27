"""Closed-file deployment snapshots. Never copy or rewind the external restore guard."""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess


def safe(root, relative):
    path = root / relative
    if Path(relative).is_absolute() or '..' in Path(relative).parts:
        raise ValueError('invalid_snapshot_path')
    current = path
    while current != root.parent:
        if current.is_symlink():
            raise ValueError('snapshot_symlink')
        current = current.parent
    return path


def databases(root):
    result = []
    for relative in ('picer.db', 'accounts-v2/control/control.db'):
        if safe(root, relative).is_file():
            result.append(relative)
    spaces = safe(root, 'accounts-v2/spaces')
    if spaces.exists():
        for directory in sorted(spaces.iterdir()):
            if not re.fullmatch('[a-f0-9]{32}', directory.name):
                raise ValueError('unexpected_space_directory')
            relative = str(directory.relative_to(root) / 'picer.db')
            if safe(root, relative).is_file():
                result.append(relative)
    if not result:
        raise ValueError('no_databases')
    return result


def digest(path):
    with path.open('rb') as stream:
        result = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(block)
        return result.hexdigest()


def assert_closed(root, names):
    for name in names:
        for suffix in ('', '-wal', '-shm', '.access'):
            path = safe(root, name + suffix)
            if path.exists():
                result = subprocess.run(['fuser', str(path)], capture_output=True)
                if result.returncode != 1:
                    raise RuntimeError('database_open_or_inspection_failed')


def schema(path):
    with contextlib.closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)) as connection:
        return connection.execute('PRAGMA user_version').fetchone()[0]


def copy_file(source, target, locked=None):
    target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = target.with_name(target.name + '.deploy-tmp')
    with (contextlib.nullcontext(locked) if locked else source.open('rb')) as incoming, temporary.open('xb') as outgoing:
        incoming.seek(0)
        shutil.copyfileobj(incoming, outgoing)
        outgoing.flush()
        os.fsync(outgoing.fileno())
    shutil.copystat(source, temporary)
    if hasattr(os, 'chown'):
        metadata = source.stat()
        os.chown(temporary, metadata.st_uid, metadata.st_gid)
    os.replace(temporary, target)
    if os.name == 'posix':
        descriptor = os.open(target.parent, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


def execute(mode, root, backup):
    root = Path(root).absolute()
    backup = Path(backup).absolute()
    names = databases(root)
    if mode == 'preflight':
        for name in names:
            if safe(root, name + '.migration.pending.json').exists():
                raise RuntimeError('unfinished_migration')
        # Snapshot + staged migration + recoverable migration backup + executable margin.
        size = sum(safe(root, name + suffix).stat().st_size for name in names
                   for suffix in ('', '-wal', '-shm') if safe(root, name + suffix).exists())
        if shutil.disk_usage(root).free < size * 4 + 512 * 1024 * 1024:
            raise RuntimeError('insufficient_upgrade_space')
        return len(names)
    assert_closed(root, names)
    control = safe(root, 'accounts-v2/control/control.db')
    control_schema = schema(control) if control.exists() else None
    # Both .NET FileShare.None and Linux flock use flock on the runtime lease.
    import fcntl
    with contextlib.ExitStack() as stack:
        handles = {}
        accounts = safe(root, 'accounts-v2')
        if accounts.exists():
            lease = stack.enter_context(safe(root, 'accounts-v2/service-runtime.lock').open('a+b'))
            fcntl.flock(lease, fcntl.LOCK_EX | fcntl.LOCK_NB)
        for name in names:
            for suffix in ('', '-wal', '-shm'):
                path = safe(root, name + suffix)
                if path.exists():
                    handle = stack.enter_context(path.open('r+b'))
                    handles[name + suffix] = handle
                    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    fcntl.lockf(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if mode == 'backup':
            execute('preflight', root, backup)
            backup.mkdir(mode=0o700, parents=True, exist_ok=False)
            entries = {}
            for name in names:
                for suffix in ('', '-wal', '-shm'):
                    source = safe(root, name + suffix)
                    if source.exists():
                        target = safe(backup, name + suffix)
                        copy_file(source, target, handles[name + suffix])
                        entries[name + suffix] = digest(target)
            control = safe(root, 'accounts-v2/control/control.db')
            manifest = {'databases': names, 'files': entries,
                        'controlSchema': control_schema}
            with safe(backup, 'snapshot.json').open('x', encoding='utf-8') as output:
                json.dump(manifest, output)
                output.flush()
                os.fsync(output.fileno())
            descriptor = os.open(backup, os.O_RDONLY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        elif mode == 'restore':
            manifest = json.loads(safe(backup, 'snapshot.json').read_text(encoding='utf-8'))
            if names != manifest['databases']:
                raise RuntimeError('database_set_changed_manual_recovery_required')
            for relative, expected in manifest['files'].items():
                if digest(safe(backup, relative)) != expected:
                    raise RuntimeError('snapshot_hash_mismatch')
            control = safe(root, 'accounts-v2/control/control.db')
            if control_schema != manifest['controlSchema']:
                raise RuntimeError('control_schema_changed_manual_recovery_required')
            for name in names:
                # Keep password changes, device revocations and registrations made since backup.
                if name == 'accounts-v2/control/control.db':
                    continue
                for suffix in ('', '-wal', '-shm'):
                    target = safe(root, name + suffix)
                    if name + suffix in manifest['files']:
                        copy_file(safe(backup, name + suffix), target)
                    elif target.exists():
                        target.unlink()
                pending = safe(root, name + '.migration.pending.json')
                if pending.exists():
                    destination = safe(backup, name + '.failed-migration.pending.json')
                    os.replace(pending, destination)
        else:
            raise ValueError('unknown_mode')
    return len(names)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['preflight', 'backup', 'restore'])
    parser.add_argument('root')
    parser.add_argument('backup')
    arguments = parser.parse_args()
    try:
        print(json.dumps({'databases': execute(arguments.mode, arguments.root, arguments.backup)}))
    except Exception as error:
        # No database paths, account IDs or SQL payloads in deployment output.
        print('snapshot operation failed: ' + type(error).__name__, file=__import__('sys').stderr)
        raise SystemExit(2)

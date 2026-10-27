import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('snapshot', Path(__file__).resolve().parents[2] / 'tools/indexservice-data-snapshot.py')
snapshot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(snapshot)


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / 'data'
        self.backup = Path(self.temporary.name) / 'backup'
        self.space = 'accounts-v2/spaces/' + 'a' * 32 + '/picer.db'
        self.control = 'accounts-v2/control/control.db'
        for name in ('picer.db', self.space, self.control):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            with sqlite3.connect(path) as connection:
                connection.execute('CREATE TABLE test(value INTEGER)')
                connection.execute('INSERT INTO test VALUES (1)')
            connection.close()

    def change(self, name, value):
        with sqlite3.connect(self.root / name) as connection:
            connection.execute('UPDATE test SET value=?', (value,))
        connection.close()

    def value(self, name):
        connection = sqlite3.connect(self.root / name)
        try:
            return connection.execute('SELECT value FROM test').fetchone()[0]
        finally:
            connection.close()

    def test_restore_all_indexes_preserves_auth_and_guard(self):
        guard = self.root.parent / 'picer-restore-guard'
        guard.write_text('new-epoch')
        self.assertEqual(3, snapshot.execute('backup', self.root, self.backup))
        for name in ('picer.db', self.space, self.control):
            self.change(name, 2)
        snapshot.execute('restore', self.root, self.backup)
        self.assertEqual(1, self.value('picer.db'))
        self.assertEqual(1, self.value(self.space))
        self.assertEqual(2, self.value(self.control))
        self.assertEqual('new-epoch', guard.read_text())

    def test_corrupt_snapshot_refuses_any_restore(self):
        snapshot.execute('backup', self.root, self.backup)
        (self.backup / self.space).write_bytes(b'bad')
        self.change('picer.db', 2)
        with self.assertRaisesRegex(RuntimeError, 'hash_mismatch'):
            snapshot.execute('restore', self.root, self.backup)
        self.assertEqual(2, self.value('picer.db'))

    def test_new_space_requires_manual_recovery(self):
        snapshot.execute('backup', self.root, self.backup)
        new = self.root / self.space.replace('a' * 32, 'b' * 32)
        new.parent.mkdir()
        new.write_bytes((self.root / self.space).read_bytes())
        with self.assertRaisesRegex(RuntimeError, 'database_set_changed'):
            snapshot.execute('restore', self.root, self.backup)

    def test_symlink_and_escape_rejected(self):
        with self.assertRaises(ValueError):
            snapshot.safe(self.root, '../outside')
        path = self.root / self.space
        path.unlink()
        path.symlink_to(self.root / 'picer.db')
        with self.assertRaisesRegex(ValueError, 'symlink'):
            snapshot.databases(self.root)

    def test_open_writer_rejected(self):
        with (self.root / self.space).open('rb'):
            with self.assertRaisesRegex(RuntimeError, 'database_open'):
                snapshot.execute('backup', self.root, self.backup)


if __name__ == '__main__':
    unittest.main()

#!/usr/bin/env bash
set -Eeuo pipefail

standalone=0
if (( $# == 0 )); then
    script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
    shopt -s nullglob
    packages=("$script_dir"/picer-index-*.zip)
    (( ${#packages[@]} == 1 )) || { echo 'standalone mode requires exactly one picer-index-*.zip beside the script' >&2; exit 2; }
    archive=${packages[0]}
    manifest_tmp=$(mktemp)
    unzip -p "$archive" deployment.json > "$manifest_tmp"
    expected_hash=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["binarySha256"])' "$manifest_tmp")
    deployment_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["deploymentId"])' "$manifest_tmp")
    rm -f "$manifest_tmp"
    standalone=1
else
    archive=${1:?usage: deploy-indexservice-linux.sh [ARCHIVE SHA256 DEPLOYMENT_ID]}
    expected_hash=${2:?missing expected executable sha256}
    deployment_id=${3:?missing deployment id}
fi
[[ $expected_hash =~ ^[a-f0-9]{64}$ ]] || { echo 'invalid expected SHA-256' >&2; exit 2; }
[[ $deployment_id =~ ^[0-9]{8}T[0-9]{9}Z$ ]] || { echo 'invalid deployment id' >&2; exit 2; }
if (( standalone == 0 )); then
    [[ $archive == "/tmp/picer-index-$deployment_id.zip" ]] || { echo 'unexpected package path' >&2; exit 2; }
fi
[[ $EUID -eq 0 ]] || { echo 'run this installer as root' >&2; exit 2; }

install_dir=/opt/picer-index
service=picer-index.service
timer=picer-index-health.timer
health_unit=picer-index-health.service
stage="/tmp/picer-index-stage-$deployment_id"
backup="/opt/picer-index.backup-$deployment_id"
new_binary="$install_dir/Picer.IndexService.new-$deployment_id"
new_manifest="$install_dir/deployment.json.new-$deployment_id"
previous_timer_state=inactive
changed=0
success=0
stopped=0
stage_created=0
backup_ready=0
rollback_ok=1
data_root=${PICER_DEPLOY_DATA_ROOT:-/var/lib/picer-index}
[[ "$data_root" == /var/lib/picer-index ]] || { echo 'unexpected database root' >&2; exit 2; }
read -r -a extra_writers <<< "${PICER_DEPLOY_WRITER_UNITS:-}"
for unit in "${extra_writers[@]}"; do
    [[ $unit =~ ^[a-zA-Z0-9_.@-]+\.service$ ]] || { echo 'invalid additional writer service name' >&2; exit 2; }
done
active_writers=()
command -v flock >/dev/null
exec 9>/run/lock/picer-index-deploy.lock
flock -n 9 || { echo 'another deployment is already running' >&2; exit 2; }

database_copy() {
    python3 "$stage/indexservice-data-snapshot.py" "$1" "$data_root" "$backup/database"
}


cleanup() {
    status=$?
    trap - EXIT
    if (( changed == 1 && success == 0 )); then
        echo 'Upgrade failed; restoring the closed database and previous executable.' >&2
        systemctl stop "$health_unit" >/dev/null 2>&1 || true
        systemctl stop "$service" || rollback_ok=0
        for unit in "${extra_writers[@]}"; do systemctl stop "$unit" || rollback_ok=0; done
        if (( backup_ready == 1 && rollback_ok == 1 )); then
            if (( rollback_ok == 1 )); then database_copy restore || rollback_ok=0; fi
            if (( rollback_ok == 1 )); then
                install -m 0755 "$backup/Picer.IndexService" "$install_dir/Picer.IndexService.rollback-$deployment_id" &&
                    mv -f "$install_dir/Picer.IndexService.rollback-$deployment_id" "$install_dir/Picer.IndexService" || rollback_ok=0
                if [[ -f "$backup/deployment.json" ]]; then
                    cp -p "$backup/deployment.json" "$install_dir/deployment.json" || rollback_ok=0
                else rm -f "$install_dir/deployment.json"; fi
            fi
        else rollback_ok=0; fi
    fi
    if (( stopped == 1 && rollback_ok == 1 )); then
        systemctl start "$service" || { rollback_ok=0; status=6; }
        if (( rollback_ok == 1 )); then
            for unit in "${active_writers[@]}"; do systemctl start "$unit" || { rollback_ok=0; status=6; }; done
        fi
    fi
    if (( rollback_ok == 1 )); then
        if [[ $previous_timer_state == active ]]; then systemctl start "$timer" || status=6; fi
    else
        echo 'Automatic recovery incomplete; services/timer remain stopped. Preserve the deployment backup for manual recovery.' >&2
        status=6
    fi
    rm -f "$new_binary" "$new_manifest"
    if (( stage_created == 1 )); then rm -rf "$stage"; fi
    if (( success == 1 && standalone == 0 )); then rm -f "$archive" "/tmp/picer-index-$deployment_id.sh"; fi
    exit "$status"
}
trap cleanup EXIT

[[ -f "$archive" ]] || { echo "package not found: $archive" >&2; exit 2; }
command -v unzip >/dev/null
command -v sha256sum >/dev/null
command -v curl >/dev/null
command -v python3 >/dev/null
command -v fuser >/dev/null
[[ ! -e "$stage" && ! -e "$backup" ]] || { echo 'deployment ID already used; create a fresh package' >&2; exit 2; }
[[ ! -e "$data_root/picer.db.migration.pending.json" ]] || { echo 'recover the interrupted previous migration before deploying' >&2; exit 2; }
systemctl cat "$service" >/dev/null
mkdir -p "$install_dir"
if systemctl is-active --quiet "$timer"; then previous_timer_state=active; fi

mkdir -p "$stage"
stage_created=1
python3 - "$archive" <<'PYZIP'
import stat,sys,zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    expected={'Picer.IndexService','deployment.json','licenses/Magick.NET-Notice.txt','indexservice-data-snapshot.py'}
    entries=archive.infolist()
    if len(entries)!=len(expected) or {item.filename for item in entries}!=expected or any(stat.S_ISLNK(item.external_attr>>16) for item in entries):
        raise SystemExit('unexpected archive entries')
PYZIP
unzip -q "$archive" -d "$stage"
binary="$stage/Picer.IndexService"
manifest="$stage/deployment.json"
[[ -f "$binary" && -f "$manifest" ]] || { echo 'package missing executable or deployment manifest' >&2; exit 2; }
actual_hash=$(sha256sum "$binary" | cut -d ' ' -f 1)
[[ "$actual_hash" == "$expected_hash" ]] || { echo 'uploaded executable SHA-256 mismatch' >&2; exit 3; }
manifest_hash=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["binarySha256"])' "$manifest")
manifest_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["deploymentId"])' "$manifest")
[[ "$manifest_hash" == "$expected_hash" && "$manifest_id" == "$deployment_id" ]] || { echo 'deployment manifest does not match uploaded package' >&2; exit 3; }

if [[ -e "$backup" ]]; then
    [[ -f "$backup/Picer.IndexService" ]] || { echo 'existing backup is incomplete; refusing to overwrite it' >&2; exit 2; }
else
    mkdir -m 0750 "$backup"
    if [[ -f "$install_dir/Picer.IndexService" ]]; then cp -p "$install_dir/Picer.IndexService" "$backup/Picer.IndexService"; fi
    if [[ -f "$install_dir/deployment.json" ]]; then cp -p "$install_dir/deployment.json" "$backup/deployment.json"; fi
fi
echo "Backup created: $backup"

database_copy preflight

# Prevent the periodic health timer from racing the controlled stop/replacement.
stopped=1
systemctl stop "$timer"
systemctl stop "$health_unit" || true
for unit in "${extra_writers[@]}"; do
    if systemctl is-active --quiet "$unit"; then active_writers+=("$unit"); fi
    systemctl stop "$unit"
done
systemctl stop "$service"
stopped=1
for _ in {1..20}; do
    if ! systemctl is-active --quiet "$service"; then break; fi
    sleep 0.5
done
if systemctl is-active --quiet "$service"; then echo 'service did not stop; refusing to replace executable' >&2; exit 4; fi

# Schema migrations require a closed-database whole-file backup, including WAL/SHM.
database_copy backup
backup_ready=1
[[ -f "$backup/Picer.IndexService" ]] || { echo 'previous executable required for rollback' >&2; exit 4; }
[[ -f "$stage/licenses/Magick.NET-Notice.txt" ]] || { echo 'Magick license notice missing' >&2; exit 4; }
mkdir -p "$install_dir/licenses"
install -m 0644 "$stage/licenses/Magick.NET-Notice.txt" "$install_dir/licenses/Magick.NET-Notice.txt"

install -m 0755 "$binary" "$new_binary"
install -m 0644 "$manifest" "$new_manifest"
changed=1
mv -f "$new_binary" "$install_dir/Picer.IndexService"
mv -f "$new_manifest" "$install_dir/deployment.json"
# Migrate every existing tenant while the listener and all writers are stopped.
# The public health endpoint alone would leave lazy-opened spaces unchecked.
service_user=$(systemctl show "$service" -p User --value)
if [[ -z "$service_user" || "$service_user" == root ]]; then
    PICER_INDEX_DATA_ROOT="$data_root" timeout 1800 "$install_dir/Picer.IndexService" --prepare-index-upgrade
else
    runuser -u "$service_user" -- env PICER_INDEX_DATA_ROOT="$data_root" timeout 1800 "$install_dir/Picer.IndexService" --prepare-index-upgrade
fi
database_copy capacity
systemctl start "$service"

verified=0
public_origin=$(systemctl show "$service" -p Environment --value | tr ' ' '\n' | sed -n 's/^PICER_INDEX_PUBLIC_ORIGIN=//p' | tail -n 1)
if [[ -n "$public_origin" ]]; then
    probe_origin="$public_origin"
    probe_host=${public_origin#*://}
    probe_host=${probe_host%%:*}
    probe_resolve=(--resolve "$probe_host:8445:127.0.0.1")
else
    probe_origin='https://127.0.0.1:8445'
    probe_resolve=()
fi
for attempt in {1..300}; do
    if (( attempt % 15 == 0 )); then echo "Waiting for schema migration and service health ($attempt/300)..."; fi
    if systemctl is-active --quiet "$service"; then
        version=$(curl "${probe_resolve[@]}" --insecure --silent --show-error --max-time 3 "$probe_origin/version" 2>/dev/null || true)
        health=$(curl "${probe_resolve[@]}" --insecure --silent --show-error --max-time 3 "$probe_origin/health" 2>/dev/null || true)
        if [[ "$version" == *"$expected_hash"* && "$version" == *"$deployment_id"* && "$health" == *'"status":"ok"'* ]]; then
            verified=1
            break
        fi
    fi
    sleep 2
done

if (( verified != 1 )); then
    echo 'new service did not report the expected deployment identity and healthy status' >&2
    echo 'Inspect the service journal locally; deployment output intentionally omits application logs.' >&2
    exit 5
fi

success=1

# Keep enough rollback history for recovery without allowing successful
# deployments to fill the system disk indefinitely. Only directories matching
# the deployment backup prefix at /opt's top level are eligible.
mapfile -t retained_backups < <(find /opt -maxdepth 1 -mindepth 1 -type d -name 'picer-index.backup-*' -printf '%T@ %p\n' | sort -nr | cut -d' ' -f2-)
if (( ${#retained_backups[@]} > 3 )); then
    for old_backup in "${retained_backups[@]:3}"; do
        [[ "$(dirname -- "$old_backup")" == /opt && "$(basename -- "$old_backup")" == picer-index.backup-* ]] || continue
        rm -rf -- "$old_backup"
    done
fi

echo "Upgrade successful: deploymentId=$deployment_id sha256=$expected_hash"
echo "Version endpoint: $probe_origin/version"

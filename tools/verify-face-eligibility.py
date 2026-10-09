"""Audit a SQLite backup: never initialize/migrate the live user database."""
import argparse
import json
from pathlib import Path
import sqlite3
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('database', type=Path)
parser.add_argument('--output', type=Path, default=Path('artifacts/p2-tests/face-eligibility.json'))
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
args.output.parent.mkdir(parents=True, exist_ok=True)
copy = args.output.with_suffix('.db')
with sqlite3.connect(args.database.resolve().as_uri() + '?mode=ro', uri=True) as source:
    with sqlite3.connect(copy) as destination:
        source.backup(destination)
result = subprocess.run(['dotnet', str(root / 'tools/Picer.Benchmarks/bin/Debug/net8.0/Picer.Benchmarks.dll'),
    '--face-eligibility', str(copy.resolve())], capture_output=True, text=True, encoding='utf-8', timeout=120)
if result.returncode:
    raise SystemExit('Eligibility audit failed; no live database was changed')
report = json.loads(result.stdout)
args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False))

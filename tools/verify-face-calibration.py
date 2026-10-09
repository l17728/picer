"""One validation-selected calibration, evaluated once on frozen test partitions. Never applies an artifact."""
import argparse
import importlib.util
import json
from pathlib import Path
import sqlite3
import struct
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('--database', type=Path, required=True)
parser.add_argument('--previous-report', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
if not args.database.resolve().is_relative_to(root / 'artifacts'):
    parser.error('Use an isolated database copy under artifacts')
previous = json.loads(args.previous_report.read_text(encoding='utf-8'))
snapshot = previous['snapshot']['SnapshotId']
model = previous['modelVersion']
with sqlite3.connect(args.database.resolve().as_uri() + '?mode=ro', uri=True) as db:
    rows = db.execute('''SELECT m.person_id,f.embedding,m.split FROM face_snapshot_members m
        JOIN detected_faces f ON f.id=m.face_id JOIN images i ON i.id=f.image_id AND i.status='active'
        WHERE m.snapshot_id=? AND f.model_version=? AND f.embedding IS NOT NULL ORDER BY m.face_id''', (snapshot, model)).fetchall()
    negative_rows = db.execute('''SELECT r.person_id,f.embedding,r.split FROM face_snapshot_rejections r
        JOIN detected_faces f ON f.id=r.face_id JOIN images i ON i.id=f.image_id AND i.status='active'
        WHERE r.snapshot_id=? AND f.model_version=? AND f.embedding IS NOT NULL ORDER BY r.face_id,r.person_id''', (snapshot, model)).fetchall()
def vector(blob): return list(struct.unpack('<'+'f'*(len(blob)//4), blob))
samples = [{'personId': p, 'embedding': vector(v), 'split': s} for p,v,s in rows]
negatives = [{'targetPersonId': p, 'embedding': vector(v), 'split': s, 'kind': 'exclusive' if p else 'unknown'} for p,v,s in negative_rows]
assert all(any(s['split']==partition for s in samples) for partition in ('train','validation','test'))
spec = importlib.util.spec_from_file_location('face_worker', root/'server/Picer.ModelServer/worker/face_worker.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
artifact = worker.process({'modelVersion': model, 'samples': [s for s in samples if s['split'] != 'test'],
                           'negativeSamples': [s for s in negatives if s['split']=='validation']})
centers = np.asarray([p['centroid'] for p in artifact['prototypes']], dtype=np.float32)
centers /= np.linalg.norm(centers, axis=1, keepdims=True)
thresholds = np.asarray([p['threshold'] for p in artifact['prototypes']])
identities = np.asarray([p['personId'] for p in artifact['prototypes']])
def score(items):
    matrix = np.asarray([s['embedding'] for s in items], dtype=np.float32)
    return (matrix/np.linalg.norm(matrix, axis=1, keepdims=True)) @ centers.T
test = [s for s in samples if s['split']=='test']
negative_test = [s for s in negatives if s['split']=='test']
scores = score(test)
indices = scores.argmax(axis=1)
accepted = scores[np.arange(len(test)), indices] >= thresholds[indices]
correct_identity = identities[indices] == np.asarray([s['personId'] for s in test])
accuracy = float((accepted & correct_identity).mean())
targets = np.asarray([[s['kind']!='exclusive' or person==s['targetPersonId'] for person in identities] for s in negative_test])
far = max(float(((score(negative_test)>=thresholds) & targets).any(axis=1).mean()), float((accepted & ~correct_identity).mean()))
report = {'modelVersion': model, 'recipe': 'all-training-positives-validation-only-threshold-sweep',
          'validation': artifact['metrics'], 'diagnostics': artifact['diagnostics'],
          'test': {'positiveSamples': len(test), 'negativeSamples': len(negative_test), 'accuracy': accuracy, 'far': far, 'frr': 1-accuracy},
          'passed': accuracy>=.9 and far<=.01 and 1-accuracy<=.10, 'applied': False,
          'limitation': 'One existing model space; not a multi-model ranking or a fresh unseen external test set.'}
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report), flush=True)

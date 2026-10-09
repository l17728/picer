"""Real, local-only calibration audit from an isolated curated SQLite snapshot.

Validation/test groups are frozen across rounds. No artifact is applied and no
raw embeddings, labels, names or prototypes are written to the result report.
"""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import random
import sqlite3
import struct
import subprocess
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('database', type=Path, help='isolated backup only')
parser.add_argument('--rounds', type=int, default=20)
parser.add_argument('--recreate-round', type=int, help='Recreate an explicitly selected lost artifact using the same frozen recipe')
parser.add_argument('--output', type=Path, default=Path('artifacts/p2-tests/face-quality.json'))
args = parser.parse_args()
if not 1 <= args.rounds <= 20:
    parser.error('rounds must be 1..20')
root = Path(__file__).resolve().parents[1]
if not args.database.resolve().is_relative_to((root / 'artifacts').resolve()):
    parser.error('database must be an isolated copy under artifacts')
snapshot_process = subprocess.run(['dotnet', str(root/'tools/Picer.Benchmarks/bin/Debug/net8.0/Picer.Benchmarks.dll'),
    '--face-snapshot', str(args.database.resolve())], capture_output=True, text=True, encoding='utf-8', timeout=120)
if snapshot_process.returncode:
    raise SystemExit('Snapshot creation failed on isolated copy')
snapshot = json.loads(snapshot_process.stdout)
database = sqlite3.connect(args.database.resolve().as_uri()+'?mode=ro', uri=True)
versions = database.execute('''SELECT f.model_version,COUNT(*),COUNT(DISTINCT m.person_id)
    FROM face_snapshot_members m JOIN detected_faces f ON f.id=m.face_id
    JOIN images i ON i.id=f.image_id AND i.status='active'
    WHERE m.snapshot_id=? AND f.embedding IS NOT NULL GROUP BY f.model_version ORDER BY COUNT(*) DESC''', (snapshot['SnapshotId'],)).fetchall()
if not versions:
    raise SystemExit('No confirmed model-specific vectors available')
model = versions[0][0]
rows = database.execute('''SELECT m.face_id,m.person_id,f.embedding,m.split,m.leakage_group
    FROM face_snapshot_members m JOIN detected_faces f ON f.id=m.face_id
    JOIN images i ON i.id=f.image_id AND i.status='active'
    WHERE m.snapshot_id=? AND f.model_version=? AND f.embedding IS NOT NULL ORDER BY m.face_id''',
    (snapshot['SnapshotId'], model)).fetchall()
def vector(blob):
    return list(struct.unpack('<'+'f'*(len(blob)//4), blob))
samples = [{'faceId': fid, 'personId': person, 'embedding': vector(blob), 'split': split,
    'evidence': {'source': 'user-confirmed', 'modelVersion': model, 'groupId': str(group)}}
    for fid, person, blob, split, group in rows]
negative_rows = database.execute('''SELECT r.face_id,r.person_id,f.embedding,r.split
    FROM face_snapshot_rejections r JOIN detected_faces f ON f.id=r.face_id
    JOIN images i ON i.id=f.image_id AND i.status='active'
    WHERE r.snapshot_id=? AND f.model_version=? AND f.embedding IS NOT NULL ORDER BY r.face_id,r.person_id''',
    (snapshot['SnapshotId'], model)).fetchall()
negatives = [{'faceId': fid, 'targetPersonId': person, 'embedding': vector(blob), 'split': split,
    'kind': 'exclusive' if person else 'unknown'} for fid, person, blob, split in negative_rows]
database.close()
report = {'snapshot': snapshot, 'modelVersion': model,
    'modelCoverage': [{'modelVersion': m, 'samples': n, 'persons': p} for m,n,p in versions],
    'partitions': dict(collections.Counter(s['split'] for s in samples)),
    'negativePartitions': dict(collections.Counter(s['split'] for s in negatives)),
    'rounds': [], 'applied': False}
if args.output.exists():
    previous = json.loads(args.output.read_text(encoding='utf-8'))
    if previous.get('snapshot',{}).get('ManifestHash') == snapshot['ManifestHash'] and previous.get('modelVersion') == model:
        report['rounds'] = [row for row in previous.get('rounds',[]) if row.get('status') == 'completed']
        report['failedAttempts'] = previous.get('failedAttempts',[]) + [row for row in previous.get('rounds',[]) if row.get('status') != 'completed']
        report['recreatedArtifacts'] = previous.get('recreatedArtifacts',[])
        if args.recreate_round:
            report['recreatedArtifacts'] += [row for row in report['rounds'] if row['round']==args.recreate_round]
            report['rounds'] = [row for row in report['rounds'] if row['round']!=args.recreate_round]
args.output.parent.mkdir(parents=True, exist_ok=True)
def save():
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
save()
print(json.dumps({k:v for k,v in report.items() if k not in ('snapshot','rounds','failedAttempts')}, ensure_ascii=False), flush=True)
if not all(any(s['split']==split for s in samples) for split in ('train','validation','test')):
    raise SystemExit('Independent training/validation/test data missing')
if not any(s['split']=='validation' for s in negatives):
    raise SystemExit('Independent validation negatives missing')
key = Path('D:/PicerData/secrets/model-server-api-key.txt').read_text(encoding='utf-8-sig').strip()
def request(route, payload=None, raw=False):
    req = urllib.request.Request('http://127.0.0.1:8091/api/face-training/'+route,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={'Authorization':'Bearer '+key, 'Content-Type':'application/json'})
    with urllib.request.urlopen(req, timeout=180) as response:
        text = response.read().decode('utf-8')
        return text if raw else json.loads(text)

for attempt in range(1, args.rounds+1):
    if any(row['round']==attempt for row in report['rounds']): continue
    # Different deterministic training subsets; validation/test are never sampled.
    rng = random.Random(20261008+attempt)
    grouped = collections.defaultdict(list)
    for sample in samples:
        if sample['split']=='train': grouped[sample['personId']].append(sample)
    selected = [s for s in samples if s['split']=='validation']
    for group in grouped.values():
        selected.extend(rng.sample(group, max(1, int(len(group)*(0.8+0.01*(attempt-1))))))
    digest = hashlib.sha256(json.dumps(selected, sort_keys=True).encode()).hexdigest()
    dataset = request('datasets', {'snapshotId': snapshot['ManifestHash'], 'modelVersion': model,
        'attempt': attempt, 'samples': selected, 'negativeSamples': [n for n in negatives if n['split']=='validation'],
        'recipeFingerprint': digest, 'recipeReason': 'frozen-holdout-training-subset',
        'scenario': 'p2-real-quality', 'evaluationId': f"{snapshot['ManifestHash']}:p2:{attempt}",
        'idempotencyKey': f"p2-{digest}", 'selectedSampleCount':len(selected)})
    job = request('jobs', {'datasetId':dataset['id'], 'type':'prototype-calibration'})
    started = time.monotonic()
    while job['status'] in ('queued','running','recoverable'):
        if time.monotonic()-started > 600:
            raise SystemExit('Training exceeded 10 minute per-round deadline')
        time.sleep(2)
        job = request('jobs/'+job['id'])
    row = {'round':attempt, 'status':job['status'], 'elapsedSeconds':round(time.monotonic()-started,1),
        'recipeFingerprint':digest, 'trainingSamples':sum(s['split']=='train' for s in selected)}
    if job['status']=='completed':
        artifact = request('artifacts/'+job['artifactId'])
        row.update({'metrics':artifact['metrics'], 'sha256':artifact.get('sha256'),
            'signaturePresent':bool(artifact.get('signature')), 'artifactId':artifact['artifactId']})
    report['rounds'].append(row)
    save()
    print(json.dumps(row), flush=True)
    if job['status']!='completed':
        raise SystemExit('Training failed; detailed server errors deliberately not logged')
report['qualityGatePassed'] = any(r['metrics']['validationAccuracy'] >= .90 and
    r['metrics']['falseAcceptRate'] <= .01 and r['metrics']['falseRejectRate'] <= .10 for r in report['rounds'])
report['independentTestEvaluated'] = False
best = max(report['rounds'], key=lambda row:(row['metrics']['validationAccuracy'], -row['metrics']['falseAcceptRate']))
artifact_text = request('artifacts/'+best['artifactId'], raw=True)
artifact = json.loads(artifact_text)
signing_key = request('signing-key')
verification = subprocess.run(['dotnet',str(root/'tools/Picer.Benchmarks/bin/Debug/net8.0/Picer.Benchmarks.dll'),'--artifact-integrity'],
    input=json.dumps({'artifact':artifact_text,'publicKey':signing_key['publicKey']}), capture_output=True,
    text=True,encoding='utf-8',timeout=30)
if verification.returncode: raise SystemExit('Artifact verification tool failed')
report['selectedArtifactIntegrity'] = json.loads(verification.stdout)
if not all(report['selectedArtifactIntegrity'].values()):
    save(); raise SystemExit('Selected artifact integrity failed')
import numpy as np
test = [s for s in samples if s['split']=='test']
test_negatives = [s for s in negatives if s['split']=='test']
centers=np.asarray([p['centroid'] for p in artifact['prototypes']],dtype=np.float32)
centers/=np.linalg.norm(centers,axis=1,keepdims=True)
thresholds=np.asarray([p['threshold'] for p in artifact['prototypes']],dtype=np.float32)
identities=np.asarray([p['personId'] for p in artifact['prototypes']])
def score(items):
    matrix=np.asarray([s['embedding'] for s in items],dtype=np.float32)
    matrix/=np.linalg.norm(matrix,axis=1,keepdims=True)
    return matrix@centers.T
scores=score(test); indices=scores.argmax(axis=1)
accepted=scores[np.arange(len(test)),indices]>=thresholds[indices]
expected=np.asarray([s['personId'] for s in test])
correct=(identities[indices]==expected)&accepted
negative_scores=score(test_negatives)
targets=np.asarray([[s['kind']!='exclusive' or p==s['targetPersonId'] for p in identities] for s in test_negatives])
negative_accepts=((negative_scores>=thresholds)&targets).any(axis=1)
wrong=(identities[indices]!=expected)&accepted
accuracy=float(correct.mean()); far=max(float(negative_accepts.mean()),float(wrong.mean())); frr=1-accuracy
report['independentTestEvaluated']=True
report['independentTest']={'selectedRound':best['round'],'samples':len(test),'negativeSamples':len(test_negatives),
    'accuracy':accuracy,'falseAcceptRate':far,'falseRejectRate':frr,'passed':accuracy>=.90 and far<=.01 and frr<=.10}
report['note'] = '20 deterministic calibration subsets of one base model; not evidence that this base model beats all alternatives. No artifact was applied.'
save()

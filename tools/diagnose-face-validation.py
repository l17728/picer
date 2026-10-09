"""Aggregate validation-only separability diagnostics. Does not read test vectors or apply artifacts."""
import argparse
import json
from pathlib import Path
import sqlite3
import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=Path, required=True)
    parser.add_argument('--previous-report', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if not args.database.resolve().is_relative_to(root / 'artifacts'):
        parser.error('Use an isolated database copy under artifacts')
    previous = json.loads(args.previous_report.read_text(encoding='utf-8'))
    parameters = (previous['snapshot']['SnapshotId'], previous['modelVersion'])
    with sqlite3.connect(args.database.resolve().as_uri() + '?mode=ro', uri=True) as db:
        rows = db.execute("""SELECT m.person_id,f.embedding,m.split FROM face_snapshot_members m
            JOIN detected_faces f ON f.id=m.face_id JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE m.snapshot_id=? AND f.model_version=? AND f.embedding IS NOT NULL
              AND m.split IN ('train','validation') ORDER BY m.face_id""", parameters).fetchall()
        negatives = db.execute("""SELECT r.person_id,f.embedding FROM face_snapshot_rejections r
            JOIN detected_faces f ON f.id=r.face_id JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE r.snapshot_id=? AND f.model_version=? AND f.embedding IS NOT NULL
              AND r.split='validation' ORDER BY r.face_id,r.person_id""", parameters).fetchall()
    def normalize(matrix):
        norms = np.linalg.norm(matrix, axis=1, keepdims=True)
        if not np.isfinite(matrix).all() or (norms <= 1e-12).any():
            raise ValueError('Invalid vector evidence')
        return matrix / norms
    identities = sorted({p for p, _, split in rows if split == 'train'})
    centers = normalize(np.asarray([np.mean([np.frombuffer(blob, dtype='<f4') for p, blob, split in rows
                                             if p == person and split == 'train'], axis=0)
                                    for person in identities]))
    positives = [(p, blob) for p, blob, split in rows if split == 'validation']
    if not positives or not negatives:
        raise ValueError('Independent validation positives and negatives are required')
    scores = normalize(np.asarray([np.frombuffer(blob, dtype='<f4') for _, blob in positives])) @ centers.T
    negative_scores = normalize(np.asarray([np.frombuffer(blob, dtype='<f4') for _, blob in negatives])) @ centers.T
    best = scores.argmax(axis=1)
    correct = np.asarray(identities)[best] == np.asarray([p for p, _ in positives])
    # A diagnostic policy, not a global optimum: each identity allows at most
    # 1% of its own reviewed negative comparisons. Include positive impostors.
    thresholds = []
    coverage = []
    for column, person in enumerate(identities):
        reviewed = [negative_scores[n, column] for n, (p, _) in enumerate(negatives) if not p or p == person]
        impostors = [scores[n, column] for n, (p, _) in enumerate(positives) if p != person]
        evidence = np.sort(np.asarray(reviewed + impostors, dtype=np.float64))[::-1]
        thresholds.append(float(np.nextafter(evidence[int(len(evidence) * .01)], np.inf)))
        coverage.append(len(reviewed))
    thresholds = np.asarray(thresholds)
    accepted = scores[np.arange(len(positives)), best] >= thresholds[best]
    targets = np.asarray([[not p or p == person for person in identities] for p, _ in negatives])
    far = max(float(((negative_scores >= thresholds) & targets).any(axis=1).mean()),
              float((accepted & ~correct).mean()))
    accuracy = float((accepted & correct).mean())
    report = {'modelVersion': parameters[1], 'persons': len(identities),
              'trainingSamples': sum(split == 'train' for _, _, split in rows),
              'validationPositives': len(positives), 'validationNegatives': len(negatives),
              'nearestCentroidIdentityAccuracyWithoutRejection': float(correct.mean()),
              'perIdentityNegativeCapPolicy': {'accuracy': accuracy, 'far': far, 'frr': 1 - accuracy,
                                              'minReviewedNegativesPerIdentity': min(coverage)},
              'testPartitionRead': False, 'applied': False,
              'limitation': 'Validation-selected diagnostic, not independent acceptance or an optimal threshold proof.'}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()

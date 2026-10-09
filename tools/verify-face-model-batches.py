"""Synthetic local model compatibility smoke; not face recognition quality evidence."""
import argparse
import base64
import gc
import importlib.util
import json
import os
from pathlib import Path
import time
import cv2
import numpy as np


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    spec = importlib.util.spec_from_file_location('face_worker', root / 'server/Picer.ModelServer/worker/face_worker.py')
    worker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(worker)
    os.environ['PICER_GPU_WORKER_USE_CUDA'] = '0'
    crop = np.random.default_rng(42).integers(0, 256, (112, 112, 3), dtype=np.uint8)
    encoded = base64.b64encode(cv2.imencode('.png', crop)[1]).decode()
    results = []
    for model in ('sface', 'insightface-arcface', 'arcface', 'adaface', 'arcface-paddle', 'facex-zoo'):
        started = time.monotonic()
        try:
            output = worker.enrich_insightface_embeddings([{'faceImageBase64': encoded} for _ in range(3)], model)
            vectors = np.asarray([s['embedding'] for s in output])
            passed = vectors.shape[0] == 3 and np.isfinite(vectors).all() and np.allclose(np.linalg.norm(vectors, axis=1), 1)
            results.append({'model': model, 'passed': bool(passed), 'dimensions': int(vectors.shape[1]),
                            'seconds': round(time.monotonic() - started, 3)})
        except Exception as exc:
            results.append({'model': model, 'passed': False, 'errorType': type(exc).__name__})
        finally:
            worker._SESSION_CACHE.clear()
            gc.collect()
        print(json.dumps(results[-1]), flush=True)
    report = {'provider': 'CPU', 'syntheticOnly': True, 'qualityAccepted': False, 'results': results}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    return 0 if all(r['passed'] for r in results) else 1


if __name__ == '__main__':
    raise SystemExit(main())

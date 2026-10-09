"""Inference contract regressions with synthetic crops and a controlled session."""
import base64
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import cv2
import numpy as np

spec = importlib.util.spec_from_file_location('face_worker', Path(__file__).resolve().parents[1] /
                                             'server/Picer.ModelServer/worker/face_worker.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class BatchTests(unittest.TestCase):
    def setUp(self):
        worker._SESSION_CACHE.clear()

    def run_crops(self, batch, count, invalid=None):
        sizes = []
        def infer(_, inputs):
            size = len(inputs['input'])
            sizes.append(size)
            vectors = np.tile([3., 4.], (size, 1))
            if invalid == 'short':
                vectors = vectors[:-1]
            elif invalid == 'nan':
                vectors[0, 0] = np.nan
            elif invalid == 'zero':
                vectors[0] = 0
            return [vectors]
        session = SimpleNamespace(get_inputs=lambda: [SimpleNamespace(name='input', shape=[batch, 3, 112, 112])],
                                  run=infer, get_providers=lambda: ['CPUExecutionProvider'])
        encoded = base64.b64encode(cv2.imencode('.png', np.zeros((112, 112, 3), np.uint8))[1]).decode()
        known = {'embedding': [1., 0.], 'marker': 'preserve'}
        samples = [{'faceImageBase64': encoded, 'marker': n} for n in range(count)]
        samples.insert(1, known)
        with patch.dict(worker.os.environ, {'PICER_GPU_WORKER_USE_CUDA': '0'}), \
                patch('onnxruntime.InferenceSession', return_value=session):
            output = worker.enrich_insightface_embeddings(samples, 'insightface-arcface')
        self.assertIs(output[1], known)
        self.assertEqual([s['marker'] for s in samples], [s['marker'] for s in output])
        for sample in output:
            self.assertAlmostEqual(1., np.linalg.norm(sample['embedding']))
        self.assertNotIn('embedding', samples[0])
        return sizes

    def test_static_one(self):
        self.assertEqual([1, 1, 1], self.run_crops(1, 3))

    def test_static_remainder_is_padded_and_discarded(self):
        self.assertEqual([4, 4], self.run_crops(4, 5))

    def test_dynamic_batch_is_bounded(self):
        self.assertEqual([16, 16, 3], self.run_crops('batch', 35))

    def test_precomputed_vectors_do_not_load_a_model(self):
        samples = [{'embedding': [1., 0.]}]
        with patch('onnxruntime.InferenceSession', side_effect=AssertionError('unnecessary model load')):
            self.assertIs(samples, worker.enrich_insightface_embeddings(samples, 'insightface-arcface'))

    def test_invalid_outputs_fail_closed(self):
        for invalid in ('short', 'nan', 'zero'):
            with self.subTest(invalid=invalid), self.assertRaisesRegex(ValueError, 'invalid'):
                self.run_crops(1, 2, invalid)


if __name__ == '__main__':
    unittest.main()

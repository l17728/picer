"""Synthetic regression tests for metrics only, not real face quality evidence."""
import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).resolve().parents[1] / 'server/Picer.ModelServer/worker/face_worker.py'
spec = importlib.util.spec_from_file_location('face_worker', path)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class MetricsTests(unittest.TestCase):
    def dataset(self):
        return {'modelVersion': 'synthetic-metric-test', 'samples': [
            {'personId': 1, 'embedding': [1., 0.], 'split': 'train'},
            {'personId': 2, 'embedding': [0., 1.], 'split': 'train'},
            {'personId': 1, 'embedding': [1., 0.], 'split': 'validation'},
            {'personId': 2, 'embedding': [0., 1.], 'split': 'validation'}],
            'negativeSamples': [{'targetPersonId': 1, 'embedding': [0., 1.], 'split': 'validation', 'kind': 'exclusive'}]}

    def test_no_validation(self):
        data = self.dataset()
        data['samples'] = data['samples'][:2]
        with self.assertRaisesRegex(ValueError, 'validation'):
            worker.process(data)

    def test_no_train(self):
        data = self.dataset()
        data['samples'] = data['samples'][2:]
        with self.assertRaisesRegex(ValueError, 'training'):
            worker.process(data)

    def test_exclusive_negative_only_rejects_target(self):
        metrics = worker.process(self.dataset())['metrics']
        self.assertEqual(0, metrics['falseAcceptRate'])
        self.assertEqual(1, metrics['validationAccuracy'])

    def test_missing_negatives_fail_closed(self):
        data = self.dataset()
        data['negativeSamples'] = []
        self.assertEqual(1, worker.process(data)['metrics']['falseAcceptRate'])

    def test_calibration_updates_artifact_and_does_not_read_test_partition(self):
        data = self.dataset()
        data['negativeSamples'][0]['embedding'] = [.95, .31225]
        result = worker.process(data)
        self.assertGreater(result['diagnostics']['appliedThresholdScale'], 1)
        self.assertGreater(result['prototypes'][0]['threshold'], .95)
        self.assertEqual(0, result['metrics']['falseAcceptRate'])
        self.assertEqual(1, result['metrics']['validationAccuracy'])
        data['negativeSamples'].append({'embedding': [1., 0.], 'split': 'test', 'kind': 'unknown'})
        data['samples'].append({'personId': 1, 'embedding': [-1., 0.], 'split': 'test'})
        self.assertEqual(result['prototypes'], worker.process(data)['prototypes'])

    def test_rejected_positive_and_train_accuracy(self):
        data = self.dataset()
        data['samples'][-1]['embedding'] = [-1., 0.]
        metrics = worker.process(data)['metrics']
        self.assertEqual(.5, metrics['falseRejectRate'])
        self.assertEqual(.5, metrics['validationAccuracy'])
        self.assertEqual(1, metrics['trainAccuracy'])

    def test_negatives_not_diluted_by_positives(self):
        data = self.dataset()
        data['samples'] += [dict(data['samples'][2]) for _ in range(100)]
        data['negativeSamples'][0]['embedding'] = [1., 0.]
        self.assertEqual(1, worker.process(data)['metrics']['falseAcceptRate'])


if __name__ == '__main__':
    unittest.main()

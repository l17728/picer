import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('asr_comparison', Path(__file__).resolve().parents[1]/'tools/verify-asr-comparison.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)

class MetricTests(unittest.TestCase):
    def test_chinese_normalization_and_substitution(self):
        self.assertEqual('你好abc123', audit.normalized('你好，ＡＢＣ １２３！'))
        self.assertEqual(1, audit.distance('你好世界', '你好世间'))

    def test_insert_delete_and_silence(self):
        self.assertEqual(2, audit.distance('你好世界', '你好'))
        self.assertEqual(2, audit.distance('', '你好'))
        self.assertEqual(0, audit.distance('', ''))

if __name__ == '__main__': unittest.main()

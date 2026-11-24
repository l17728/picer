"""The ModelServer worker environment is pinned in two places that must agree.

``server/modelserver-requirements-<device>.txt`` is what the installer installs and the
``PROBE`` inside ``server/setup-modelserver.py`` is what it verifies afterwards. A pin that
drifts between them makes every freshly built environment fail its own check — which is how
``onnxruntime-gpu==1.20.1`` (a version that was never published) stayed unnoticed.
"""

import re
import unittest
from pathlib import Path

SERVER = Path(__file__).resolve().parents[2] / "server"


def pins(path: Path) -> dict[str, str]:
    result = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^\s*([A-Za-z0-9_.-]+)\s*==\s*([A-Za-z0-9_.+]+)\s*$", line)
        if match:
            result[match.group(1).lower()] = match.group(2)
    return result


class ModelServerPinTests(unittest.TestCase):
    def test_probe_versions_match_both_requirement_files(self):
        script = (SERVER / "setup-modelserver.py").read_text(encoding="utf-8")
        probe = re.search(r"PROBE = r'''(.*?)'''", script, re.S)
        self.assertIsNotNone(probe, "PROBE block not found in setup-modelserver.py")
        cpu = pins(SERVER / "modelserver-requirements-cpu.txt")
        cuda = pins(SERVER / "modelserver-requirements-cuda.txt")

        # The probe must expect exactly what the requirement files install.
        self.assertIn(f'"numpy": "{cpu["numpy"]}"', probe.group(1))
        self.assertIn(f'"opencv-python-headless": "{cpu["opencv-python-headless"]}"', probe.group(1))
        self.assertIn(f'"onnxruntime-gpu" if cfg["cuda"] else "onnxruntime": "{cuda["onnxruntime-gpu"]}" if cfg["cuda"] else "{cpu["onnxruntime"]}"',
                      probe.group(1))

        for device, pinned in (("cpu", cpu), ("cuda", cuda)):
            runtime = "onnxruntime" if device == "cpu" else "onnxruntime-gpu"
            with self.subTest(device=device):
                self.assertEqual(cpu["numpy"], pinned["numpy"])
                self.assertEqual(cpu["opencv-python-headless"], pinned["opencv-python-headless"])
                # Exactly one ONNX Runtime flavour per environment: mixing them breaks providers.
                self.assertIn(runtime, pinned)
                self.assertNotIn("onnxruntime-gpu" if device == "cpu" else "onnxruntime", pinned)

    def test_torch_pin_is_shared_by_installer_and_probe(self):
        script = (SERVER / "setup-modelserver.py").read_text(encoding="utf-8")
        declared = re.search(r'^TORCH_VERSION = "([^"]+)"', script, re.M)
        probed = re.search(r'torch\.__version__\.split\("\+"\)\[0\] != "([^"]+)"', script)
        self.assertIsNotNone(declared)
        self.assertIsNotNone(probed)
        self.assertEqual(declared.group(1), probed.group(1))


if __name__ == "__main__":
    unittest.main()

"""The Linux installer must recognise a service that runs inside a container.

Hosts whose glibc is older than the bundled native libraries run IndexService through
``docker run``. The installer then has to prepare the schema inside the same image and read
the public origin from the container arguments. These tests drive the detection block of the
real script with a stubbed ``systemctl`` so the parsing cannot drift silently.
"""

import re
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "deploy-indexservice-linux.sh"

DOCKER_UNIT = (
    "{ path=/usr/bin/docker ; argv[]=/usr/bin/docker run --rm --name picer-index "
    "--network host -e PICER_INDEX_DATA_ROOT=/var/lib/picer-index "
    "-e PICER_INDEX_PUBLIC_ORIGIN=https://47-103-99-229.sslip.io:8445 "
    "-v /opt/picer-index:/opt/picer-index:ro -v /var/lib/picer-index:/var/lib/picer-index:rw "
    "ubuntu:24.04 /opt/picer-index/Picer.IndexService ; ignore_errors=no ; "
    "start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }"
)
NATIVE_UNIT = (
    "{ path=/opt/picer-index/Picer.IndexService ; argv[]=/opt/picer-index/Picer.IndexService "
    "; ignore_errors=no ; start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }"
)


def detection_block() -> str:
    lines = SCRIPT.read_text(encoding="utf-8").splitlines()
    start = next(index for index, line in enumerate(lines) if line.startswith("containerized=0"))
    end = next(index for index in range(start, len(lines)) if "Service runs in container image" in lines[index])
    # The detection ends with the closing "fi" of the docker branch.
    closing = end + 1
    while lines[closing].strip() != "fi":
        closing += 1
    return "\n".join(lines[start : closing + 1])


def origin_block() -> str:
    lines = SCRIPT.read_text(encoding="utf-8").splitlines()
    start = next(index for index, line in enumerate(lines) if line.startswith("if [[ -z \"$public_origin\" && $containerized ]]"))
    end = start
    while lines[end].strip() != "fi":
        end += 1
    return "\n".join(lines[start : end + 1])


def run(block: str, unit: str, prelude: str = "") -> dict[str, str]:
    with tempfile.NamedTemporaryFile("w", suffix=".sh", delete=False, encoding="utf-8", newline="\n") as handle:
        handle.write(
            "set -Eeuo pipefail\n"
            "service=picer-index.service\n"
            "systemctl() { printf '%s\\n' '" + unit.replace("'", "'\\''") + "'; }\n"
            + prelude
            + "\n"
            + block
            + "\n"
            'printf "containerized=%s\\nimage=%s\\nexec=%s\\nargs=%s\\norigin=%s\\n" '
            '"$containerized" "$container_image" "$container_exec" "${container_args[*]}" "${public_origin:-}"\n'
        )
        path = handle.name
    completed = subprocess.run(["bash", path], capture_output=True, text=True, timeout=30)
    output = dict(line.split("=", 1) for line in completed.stdout.splitlines() if "=" in line)
    output["_exit"] = str(completed.returncode)
    output["_stderr"] = completed.stderr
    return output


class DeployContainerDetectionTests(unittest.TestCase):
    def test_docker_unit_is_recognised_with_image_executable_and_mounts(self):
        result = run(detection_block() + "\npublic_origin=''\n" + origin_block(), DOCKER_UNIT)
        self.assertEqual("0", result["_exit"], result["_stderr"])
        self.assertEqual("1", result["containerized"])
        self.assertEqual("ubuntu:24.04", result["image"])
        self.assertEqual("/opt/picer-index/Picer.IndexService", result["exec"])
        # --rm and --name are dropped (the installer supplies its own); everything else is kept.
        self.assertEqual(
            "--network host -e PICER_INDEX_DATA_ROOT=/var/lib/picer-index "
            "-e PICER_INDEX_PUBLIC_ORIGIN=https://47-103-99-229.sslip.io:8445 "
            "-v /opt/picer-index:/opt/picer-index:ro -v /var/lib/picer-index:/var/lib/picer-index:rw",
            result["args"],
        )
        self.assertEqual("https://47-103-99-229.sslip.io:8445", result["origin"])

    def test_native_unit_keeps_host_execution(self):
        result = run(detection_block() + "\npublic_origin=''\n" + origin_block(), NATIVE_UNIT)
        self.assertEqual("0", result["_exit"], result["_stderr"])
        self.assertEqual("0", result["containerized"])
        self.assertEqual("", result["image"])
        self.assertEqual("", result["origin"])

    def test_shell_metacharacters_in_unit_are_refused_before_evaluation(self):
        hostile = NATIVE_UNIT.replace("/opt/picer-index/Picer.IndexService ;", "/opt/picer-index/$(reboot) ;")
        result = run(detection_block(), hostile)
        self.assertNotEqual("0", result["_exit"])
        self.assertIn("unsupported characters", result["_stderr"])


if __name__ == "__main__":
    unittest.main()

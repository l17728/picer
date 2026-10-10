#!/usr/bin/env python3
"""Create a separate ModelServer worker venv; never deploy or start a service."""
import argparse
import json
import os
from pathlib import Path
import platform
import shlex
import struct
import subprocess
import sys

BASE = Path(__file__).resolve().parent
MODELS = {
    "sface": "PICER_SFACE_MODEL_PATH",
    "insightface-arcface": "PICER_INSIGHTFACE_MODEL_PATH",
    "arcface": "PICER_ARCFACE_MODEL_PATH",
    "adaface": "PICER_ADAFACE_MODEL_PATH",
    "arcface-paddle": "PICER_ARCFACE_PADDLE_MODEL_PATH",
    "facex-zoo": "PICER_FACEX_ZOO_MODEL_PATH",
}
TORCH_VERSION = "2.6.0"
PROBE = r'''
import importlib.metadata as md, json, runpy, sys
cfg = json.load(sys.stdin)
try:
    import torch  # Preload CUDA/cuDNN libraries before ONNX Runtime, like face_worker.
    import cv2, numpy as np, onnxruntime as ort
    expected = {"numpy": "1.26.4", "opencv-python-headless": "4.10.0.84",
                "onnxruntime-gpu" if cfg["cuda"] else "onnxruntime": "1.20.1"}
    for package, version in expected.items():
        if md.version(package) != version:
            raise RuntimeError("version")
    if torch.__version__.split("+")[0] != "2.6.0":
        raise RuntimeError("torch_version")
    opposite = "onnxruntime" if cfg["cuda"] else "onnxruntime-gpu"
    try:
        md.version(opposite)
    except md.PackageNotFoundError:
        pass
    else:
        raise RuntimeError("mixed_ort")
    if cfg["cuda"]:
        if not torch.cuda.is_available() or torch.version.cuda != "12.4":
            raise RuntimeError("cuda")
        torch.ones(1, device="cuda").sum().item()
    elif torch.version.cuda is not None:
        raise RuntimeError("not_cpu_torch")
    if cfg["model"]:
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"] if cfg["cuda"] else ["CPUExecutionProvider"]
        session = ort.InferenceSession(cfg["model"], providers=providers)
        if cfg["cuda"] and "CUDAExecutionProvider" not in session.get_providers():
            raise RuntimeError("cuda_fallback")
        cv2.FaceDetectorYN.create(cfg["detector"], "", (320, 320))
    # Numeric operation used by the worker, with no real face or media data.
    result = torch.nn.functional.cosine_similarity(torch.ones(2), torch.ones(2), dim=0).item()
    if not 0.99 < result < 1.01:
        raise RuntimeError("numeric")
    worker = runpy.run_path(cfg["worker"])
    if worker["process"]({"operation": "runtime"})["torchCudaAvailable"] != torch.cuda.is_available():
        raise RuntimeError("worker_protocol")
    print(json.dumps({"torch": torch.__version__, **{k: md.version(k) for k in expected},
                      "device": "cuda" if cfg["cuda"] else "cpu"}))
except Exception:
    # Native loader exceptions can contain paths/config; expose only an actionable summary.
    sys.exit(2)
'''


def run(command, failure, *, data=None, timeout=1800):
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(("PIP_", "PYTHON"))}
    # Ignore pip configuration/credential-bearing private indexes; use explicit official indexes.
    env["PIP_CONFIG_FILE"] = os.devnull
    env["PYTHONIOENCODING"] = "utf-8"
    try:
        result = subprocess.run(command, input=data, capture_output=True, encoding="utf-8",
                                errors="replace", env=env, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise RuntimeError(failure) from None
    if result.returncode != 0:
        raise RuntimeError(failure)
    return result.stdout


def file_path(value, instruction):
    if not value:
        raise RuntimeError(instruction)
    result = Path(value).expanduser().resolve()
    if not result.is_file():
        raise RuntimeError(instruction)
    if any(c in str(result) for c in "\r\n\0"):
        raise RuntimeError("路径不能包含换行或空字符。")
    return result


def write_environment(folder, values):
    # Only an explicit allowlist, never a dump of the current process environment.
    powershell = "\n".join("$env:" + key + " = '" + value.replace("'", "''") + "'"
                           for key, value in values.items()) + "\n"
    shell = "\n".join("export " + key + "=" + shlex.quote(value)
                      for key, value in values.items()) + "\n"
    # BOM allows Windows PowerShell 5.1 to read paths containing Chinese correctly.
    (folder / "worker-env.ps1").write_text(powershell, encoding="utf-8-sig")
    (folder / "worker-env.sh").write_text(shell, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description="安装或只读检查 ModelServer 人脸 worker 的独立 Python 环境。")
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu", help="默认 CPU；CUDA 需已安装兼容 NVIDIA 驱动")
    parser.add_argument("--venv", type=Path, help="独立环境目录；默认放在当前用户数据目录，不能覆盖陌生环境")
    parser.add_argument("--worker", help="face_worker.py；默认自动识别源码或发布包布局")
    parser.add_argument("--model", choices=list(MODELS), default="sface")
    parser.add_argument("--model-path", help="所选识别模型的 ONNX 文件；也可设置相应 PICER_*_MODEL_PATH")
    parser.add_argument("--detector-path", help="YuNet ONNX 文件；也可设置 PICER_YUNET_MODEL_PATH")
    parser.add_argument("--embeddings-only", action="store_true", help="仅检查预计算向量校准，不声称图像检测/向量提取可用")
    parser.add_argument("--check", action="store_true", help="不创建环境、不安装依赖、不写配置，仅校验")
    args = parser.parse_args()
    if not (3, 10) <= sys.version_info[:2] <= (3, 12) or struct.calcsize("P") != 8:
        raise RuntimeError("请安装 64 位 Python 3.10–3.12，并用该解释器运行此脚本（Windows: py -3.12；Linux: python3.12）。")
    if sys.platform not in ("win32", "linux") or platform.machine().lower() not in ("amd64", "x86_64"):
        raise RuntimeError("此固定依赖方案仅支持 Windows/Linux x64。")
    worker_default = next((p for p in (BASE / "worker/face_worker.py", BASE / "Picer.ModelServer/worker/face_worker.py") if p.is_file()), None)
    worker = file_path(args.worker or worker_default, "未找到 face_worker.py；请用 --worker 指定发布包中的 worker/face_worker.py。")
    requirements = BASE / ("modelserver-requirements-" + args.device + ".txt")
    if not args.check and not requirements.is_file():
        raise RuntimeError("缺少随包 requirements 文件；请将 setup-modelserver.py 与两份 modelserver-requirements-*.txt 放在同一目录。")
    model = detector = None
    if not args.embeddings_only:
        model = file_path(args.model_path or os.environ.get(MODELS[args.model]),
                          f"识别模型未配置或不存在；使用 --model-path 指向已合法取得的 ONNX，或设置 {MODELS[args.model]}。脚本不会下载模型。")
        detector = file_path(args.detector_path or os.environ.get("PICER_YUNET_MODEL_PATH"),
                             "YuNet 模型未配置或不存在；使用 --detector-path 或设置 PICER_YUNET_MODEL_PATH。")
    default_root = (Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData/Local"))) / "PicerModelWorker"
                    if sys.platform == "win32" else Path.home() / ".local/share/picer-model-worker")
    folder = (args.venv or default_root / args.device).expanduser().absolute()
    if folder.is_symlink() or (hasattr(folder, "is_junction") and folder.is_junction()):
        raise RuntimeError("venv 目录不能是符号链接或 junction；请选择独立目录。")
    folder = folder.resolve()
    if any(c in str(folder) for c in "\r\n\0"):
        raise RuntimeError("venv 路径不能包含换行或空字符。")
    marker = folder / ".picer-worker-environment.json"
    identity = {"schema": 1, "device": args.device, "python": list(sys.version_info[:2])}
    if folder.exists():
        try:
            owned = json.loads(marker.read_text(encoding="utf-8")) == identity
        except (OSError, ValueError):
            owned = False
        if not owned:
            raise RuntimeError("该目录不是本脚本创建的同版本/同设备环境；请用 --venv 指定尚不存在的新目录，不能就地切换 CPU/CUDA。")
    elif args.check:
        raise RuntimeError("venv 不存在；先去掉 --check 安装环境，或用 --venv 指向已安装目录。")
    else:
        folder.mkdir(parents=True)
        marker.write_text(json.dumps(identity), encoding="utf-8")
    python = folder / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    if not args.check:
        print("正在准备独立 venv（不修改系统 Python、驱动或服务）……", flush=True)
        run([sys.executable, "-I", "-m", "venv", str(folder)],
            "venv 创建失败；Linux 请安装对应的 python3-venv 包，Windows 请确认 Python 包含 pip/venv。")
        pip = [str(python), "-I", "-m", "pip", "--disable-pip-version-check", "--no-input",
               "--timeout", "30", "--retries", "2"]
        install = ["install", "--only-binary=:all:", "--index-url", "https://pypi.org/simple"]
        print("正在更新 venv 内的固定版本 pip……", flush=True)
        run(pip + install + ["pip==24.3.1"], "pip 安装失败；检查到 pypi.org 的网络和证书。不会输出可能含凭据的安装日志。")
        print("正在安装固定版本依赖；首次下载 PyTorch 可能需要数分钟……", flush=True)
        run(pip + install + ["-r", str(requirements)], "依赖安装失败；确认 Python 3.10–3.12 x64、磁盘空间及到 pypi.org 的网络。可用同一命令重试。")
        index = "https://download.pytorch.org/whl/" + ("cu124" if args.device == "cuda" else "cpu")
        print("正在安装 " + args.device.upper() + " 版 PyTorch……", flush=True)
        run(pip + ["install", "--only-binary=:all:", "--index-url", index, "torch==" + TORCH_VERSION],
            "PyTorch 安装失败；检查到 download.pytorch.org 的网络和磁盘空间，然后重试。")
    if not python.is_file():
        raise RuntimeError("venv 中缺少 Python；去掉 --check 重试创建，或指定新的 --venv 目录。")
    run([str(python), "-I", "-B", "-m", "pip", "check"], "依赖检查不通过；请用新的 --venv 目录重建独立环境。", timeout=60)
    config = {"cuda": args.device == "cuda", "worker": str(worker), "model": str(model) if model else None,
              "detector": str(detector) if detector else None}
    result = run([str(python), "-I", "-B", "-c", PROBE],
                 "运行检查失败：确认固定依赖版本、有效的所选 ONNX/YuNet 模型；Windows 需 VC++ x64 运行库。CUDA 模式还需兼容 CUDA 12.4 的驱动与可用 GPU；可改用新的 CPU venv。",
                 data=json.dumps(config), timeout=180)
    versions = json.loads(result)
    if not args.check:
        values = {"PICER_GPU_WORKER_PYTHON": str(python), "PICER_GPU_WORKER_SCRIPT": str(worker),
                  "PICER_GPU_WORKER_USE_CUDA": "1" if args.device == "cuda" else "0"}
        if model:
            values[MODELS[args.model]] = str(model)
            values["PICER_YUNET_MODEL_PATH"] = str(detector)
        write_environment(folder, values)
        (folder / "verified-versions.json").write_text(json.dumps(versions, indent=2), encoding="utf-8")
        # Records resolved transitive versions; contains package names/versions only, never URLs.
        packages = json.loads(run([str(python), "-I", "-m", "pip", "list", "--format=json"], "无法记录依赖版本。", timeout=60))
        (folder / "installed-versions.txt").write_text("\n".join(p["name"] + "==" + p["version"] for p in packages) + "\n", encoding="utf-8")
        print("环境已验证。服务启动前读取 venv 内 worker-env.ps1（Windows）或 worker-env.sh（Linux）。")
    else:
        print("只读检查通过。")
    print("仅预计算向量校准可用；未验证图像检测/向量提取。" if args.embeddings_only else "所选识别模型与 YuNet 已加载验证。")
    print("Python: " + str(python))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, ValueError) as error:
        # OSError may expose paths, but never emit raw subprocess/configuration output.
        message = str(error) if isinstance(error, RuntimeError) else "文件读写或配置解析失败；检查目录权限、剩余空间与参数。"
        print("配置失败：" + message, file=sys.stderr)
        sys.exit(1)

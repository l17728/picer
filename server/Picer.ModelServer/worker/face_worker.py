"""Face training worker. Reads one JSON job and writes one artifact."""
import base64, json, os, sys, uuid, datetime
import torch

_SESSION_CACHE = {}
_DETECTOR_CACHE = {}

def field(sample, camel, pascal):
    if camel in sample:
        return sample[camel]
    return sample[pascal]

def cosine(a, b):
    return torch.nn.functional.cosine_similarity(a, b, dim=0).item()

def label_of(sample):
    return sample.get("label", sample.get("Label", "positive"))

def enrich_insightface_embeddings(samples, model_version):
    """Generate model-specific vectors only when the client supplies an aligned
    112x112 face crop. Paths are intentionally unsupported for privacy."""
    supported = {"sface", "insightface-arcface", "arcface", "adaface", "arcface-paddle", "facex-zoo"}
    if model_version not in supported:
        return samples
    missing = [s for s in samples if not s.get("embedding") and not s.get("Embedding")
               and not s.get("faceImageBase64") and not s.get("FaceImageBase64")]
    if missing:
        raise ValueError("InsightFace requires faceImageBase64 for samples without embeddings")
    if all(s.get("embedding") or s.get("Embedding") for s in samples):
        return samples
    import cv2, numpy as np, onnxruntime as ort
    env_name = {
        "sface": "PICER_SFACE_MODEL_PATH",
        "insightface-arcface": "PICER_INSIGHTFACE_MODEL_PATH",
        "arcface": "PICER_ARCFACE_MODEL_PATH",
        "adaface": "PICER_ADAFACE_MODEL_PATH",
        "arcface-paddle": "PICER_ARCFACE_PADDLE_MODEL_PATH",
        "facex-zoo": "PICER_FACEX_ZOO_MODEL_PATH",
    }[model_version]
    model_path = os.environ.get(env_name, "")
    if not model_path:
        model_path = {
            "PICER_SFACE_MODEL_PATH": r"D:\picer\third_party\runtime\models\face\face_recognition_sface_2021dec.onnx",
            "PICER_INSIGHTFACE_MODEL_PATH": r"D:\VL-models\models\insightface\w600k_r50.onnx",
            "PICER_ARCFACE_MODEL_PATH": r"D:\VL-models\models\candidates\arcface.onnx",
            "PICER_ADAFACE_MODEL_PATH": r"D:\VL-models\models\candidates\adaface_ir_18.onnx",
            "PICER_ARCFACE_PADDLE_MODEL_PATH": r"D:\VL-models\models\candidates\arcface_paddle.onnx",
            "PICER_FACEX_ZOO_MODEL_PATH": r"D:\VL-models\models\candidates\facex_zoo.onnx",
        }[env_name]
    if not model_path:
        raise ValueError(f"{model_version} model path is not configured")
    # CPU is the deterministic fallback. CUDA is opt-in because an installed
    # CUDAExecutionProvider can hang on machines whose driver/runtime pair is
    # incompatible; the platform still reports the same model result.
    providers = ["CPUExecutionProvider"]
    if os.environ.get("PICER_GPU_WORKER_USE_CUDA", "").strip().lower() in {"1", "true", "yes"}:
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
    cache_key = (model_version, model_path, tuple(providers))
    session = _SESSION_CACHE.get(cache_key)
    if session is None:
        session = ort.InferenceSession(model_path, providers=providers)
        if "CUDAExecutionProvider" in providers and "CUDAExecutionProvider" not in session.get_providers():
            raise RuntimeError("CUDA was requested but the model fell back to CPU")
        _SESSION_CACHE[cache_key] = session
    input_name = session.get_inputs()[0].name
    input_shape = session.get_inputs()[0].shape
    output = list(samples)
    pending = []
    tensors = []
    # Respect static exports and bound both decoded crops and inference memory.
    static_batch = input_shape[0] if isinstance(input_shape[0], int) and input_shape[0] > 0 else None
    batch_size = static_batch or 16
    def flush():
        if not tensors:
            return
        count = len(tensors)
        padded = tensors + [tensors[-1]] * (batch_size - count) if static_batch else tensors
        vectors = np.asarray(session.run(None, {input_name: np.concatenate(padded, axis=0)})[0])
        if vectors.ndim != 2 or vectors.shape[0] != len(padded) or vectors.shape[1] == 0:
            raise ValueError("face model returned an invalid embedding shape")
        norms = np.linalg.norm(vectors[:count], axis=1)
        if not np.isfinite(vectors[:count]).all() or not np.isfinite(norms).all() or (norms <= 1e-12).any():
            raise ValueError("face model returned invalid embeddings")
        for index, vector, norm in zip(pending, vectors, norms):
            enriched = dict(samples[index])
            enriched["embedding"] = (vector / norm).astype(float).tolist()
            output[index] = enriched
        pending.clear()
        tensors.clear()
    for index, sample in enumerate(samples):
        if sample.get("embedding") or sample.get("Embedding"):
            continue
        encoded = sample.get("faceImageBase64", sample.get("FaceImageBase64"))
        raw = base64.b64decode(encoded)
        image = cv2.imdecode(np.frombuffer(raw, dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None:
            raise ValueError("invalid faceImageBase64")
        # Respect the exported model contract. ArcFace variants commonly use
        # NCHW 112x112 while Paddle/mobile exports may use NHWC or another
        # spatial size; hard-coding 112 silently produces invalid embeddings.
        shape = input_shape
        if shape[-1] == 3:
            height, width = shape[1], shape[2]
        else:
            height, width = shape[-2], shape[-1]
        height = int(height) if isinstance(height, (int, float)) else 112
        width = int(width) if isinstance(width, (int, float)) else 112
        image = cv2.resize(image, (width, height), interpolation=cv2.INTER_AREA)
        image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB).astype(np.float32)
        image = (image - 127.5) / 127.5
        tensor = image[None, ...] if input_shape[-1] == 3 else image.transpose(2, 0, 1)[None, ...]
        pending.append(index)
        tensors.append(tensor)
        if len(tensors) == batch_size:
            flush()
    flush()
    return output

def detect_and_embed(request):
    model_version = request.get("modelVersion", "insightface-arcface")
    encoded = (request.get("images") or [None])[0]
    if not encoded:
        raise ValueError("image_required")
    import cv2, numpy as np
    detector_path = os.environ.get(
        "PICER_YUNET_MODEL_PATH",
        r"D:\picer\third_party\runtime\models\face\face_detection_yunet_2023mar.onnx")
    if not os.path.isfile(detector_path):
        raise ValueError("yunet_detector_not_configured")
    detector = _DETECTOR_CACHE.get(detector_path)
    if detector is None:
        detector = cv2.FaceDetectorYN.create(detector_path, "", (320, 320), 0.65, 0.3, 5000)
        _DETECTOR_CACHE[detector_path] = detector
    raw = base64.b64decode(encoded, validate=True)
    image = cv2.imdecode(np.frombuffer(raw, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("invalid_image")
    source_height, source_width = image.shape[:2]
    scale = min(1.0, 1280.0 / max(source_width, source_height))
    if scale < 1.0:
        image = cv2.resize(image, (round(source_width * scale), round(source_height * scale)),
                           interpolation=cv2.INTER_AREA)
    height, width = image.shape[:2]
    detector.setInputSize((width, height))
    _, rows = detector.detect(image)
    faces = []
    target = np.asarray([[38.2946, 51.6963], [73.5318, 51.5014], [56.0252, 71.7366],
                         [41.5493, 92.3655], [70.7299, 92.2041]], dtype=np.float32)
    aligned_images = []
    boxes = []
    for ordinal, row in enumerate(rows if rows is not None else []):
        x, y, box_width, box_height = [float(value) for value in row[:4]]
        x0, y0 = max(0.0, x), max(0.0, y)
        x1, y1 = min(float(width), x + box_width), min(float(height), y + box_height)
        if x1 <= x0 or y1 <= y0:
            continue
        landmarks = np.asarray(row[4:14], dtype=np.float32).reshape(5, 2)
        transform, _ = cv2.estimateAffinePartial2D(landmarks, target, method=cv2.LMEDS)
        if transform is not None:
            aligned = cv2.warpAffine(image, transform, (112, 112), flags=cv2.INTER_LINEAR,
                                     borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0))
        else:
            crop = image[int(y0):int(y1), int(x0):int(x1)]
            aligned = cv2.resize(crop, (112, 112), interpolation=cv2.INTER_AREA)
        ok, encoded_crop = cv2.imencode(".jpg", aligned, [cv2.IMWRITE_JPEG_QUALITY, 95])
        if not ok:
            raise ValueError("face_crop_encode_failed")
        aligned_images.append(base64.b64encode(encoded_crop.tobytes()).decode("ascii"))
        boxes.append({"ordinal": ordinal, "x": x0 / width, "y": y0 / height,
                      "width": (x1 - x0) / width, "height": (y1 - y0) / height,
                      "confidence": float(row[-1])})
    if aligned_images:
        enriched = enrich_insightface_embeddings(
            [{"faceImageBase64": value} for value in aligned_images], model_version)
        for box, sample in zip(boxes, enriched):
            box["embedding"] = sample["embedding"]
            faces.append(box)
    return {"schemaVersion": "picer.face.v1", "operation": "detect",
            "modelVersion": model_version, "faces": faces}

def process(request):
    if request.get("operation") == "runtime":
        return {"cudaRequested": os.environ.get("PICER_GPU_WORKER_USE_CUDA", "").lower() in {"1", "true", "yes"},
                "torchCudaAvailable": torch.cuda.is_available(),
                "sessions": [{"modelVersion": key[0], "providers": session.get_providers()}
                             for key, session in _SESSION_CACHE.items()]}
    if request.get("operation") == "detect":
        return detect_and_embed(request)
    if request.get("operation") == "embed":
        model_version = request.get("modelVersion", "insightface-arcface")
        samples = [{"faceImageBase64": value} for value in request.get("images", [])]
        enriched = enrich_insightface_embeddings(samples, model_version)
        return {"modelVersion": model_version, "embeddings": [s["embedding"] for s in enriched]}
    samples = request.get("samples", [])
    negative_samples = request.get("negativeSamples", []) or []
    if len(samples) < 2:
        raise ValueError("at least two samples required")
    model_version = request.get("modelVersion", "sface")
    samples = enrich_insightface_embeddings(samples, model_version)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    train = [s for s in samples if s.get("split", s.get("Split")) == "train" and label_of(s) != "negative"]
    if not train:
        raise ValueError("independent training samples are required")
    groups = {}
    for s in train:
        person_id = int(field(s, "personId", "PersonId"))
        embedding = field(s, "embedding", "Embedding")
        groups.setdefault(person_id, []).append(embedding)
    prototypes = []
    for person, vectors in groups.items():
        matrix = torch.tensor(vectors, dtype=torch.float32, device=device)
        centroid = matrix.mean(0)
        sims = torch.nn.functional.cosine_similarity(matrix, centroid.unsqueeze(0), dim=1).cpu().tolist()
        # Calibrate each person's threshold from training positives. Hard
        # negatives are evaluated separately; they must never form a centroid.
        positives = sorted(sims)
        threshold = positives[max(0, int(len(positives) * .05) - 1)] if positives else .5
        prototypes.append({"personId": person, "centroid": centroid.detach().cpu().tolist(), "threshold": max(0.25, min(.90, threshold)), "sampleCount": len(vectors)})
    evals = [s for s in samples if s.get("split", s.get("Split")) == "validation"]
    if not any(label_of(s) != "negative" for s in evals):
        raise ValueError("independent positive validation samples are required")
    # Cache batched scores once. Per-vector CUDA .item() calls forced thousands
    # of host/device synchronizations per threshold and timed out real datasets.
    centers = torch.tensor([p["centroid"] for p in prototypes], dtype=torch.float32, device=device)
    centers = torch.nn.functional.normalize(centers, dim=1)
    thresholds = torch.tensor([p["threshold"] for p in prototypes], device=device)
    identities = torch.tensor([p["personId"] for p in prototypes], device=device)
    def scores(items):
        matrix = torch.tensor([field(s, "embedding", "Embedding") for s in items], dtype=torch.float32, device=device)
        return torch.nn.functional.normalize(matrix, dim=1) @ centers.T
    heldout_negatives = [s for s in negative_samples if s.get("split", "validation") == "validation"]
    negative_scores = scores(heldout_negatives) if heldout_negatives else None
    negative_targets = torch.tensor([[s.get("kind", "exclusive") != "exclusive" or
        p["personId"] == s.get("targetPersonId", s.get("TargetPersonId")) for p in prototypes]
        for s in heldout_negatives], dtype=torch.bool, device=device) if heldout_negatives else None
    score_cache = {}
    def evaluate(scale, evaluation_samples=None):
        items = evals if evaluation_samples is None else evaluation_samples
        cache_key = id(items)
        if cache_key not in score_cache:
            best_scores, best_indices = scores(items).max(dim=1)
            expected = torch.tensor([int(field(s, "personId", "PersonId")) for s in items], device=device)
            is_negative = torch.tensor([label_of(s) == "negative" for s in items], dtype=torch.bool, device=device)
            score_cache[cache_key] = best_scores, best_indices, expected, is_negative
        best_scores, best_indices, expected, is_negative = score_cache[cache_key]
        accepted = best_scores >= thresholds[best_indices] * scale
        correct_identity = identities[best_indices] == expected
        correct = ((~is_negative & accepted & correct_identity) | (is_negative & ~accepted)).sum().item()
        positive_total = (~is_negative).sum().item()
        false_negative = (~is_negative & ~(accepted & correct_identity)).sum().item()
        wrong_identity = (~is_negative & accepted & ~correct_identity).sum().item()
        false_accepts = (is_negative & accepted).sum().item()
        if negative_scores is not None:
            false_accepts += ((negative_scores >= thresholds.unsqueeze(0) * scale) & negative_targets).any(dim=1).sum().item()
        negative_total = len(heldout_negatives) + is_negative.sum().item()
        far = false_accepts / negative_total if negative_total else 1.0
        far = max(far, wrong_identity / (positive_total or 1))
        return correct / len(items), far, false_negative / (positive_total or 1)

    accuracy, far, frr = evaluate(1.0)
    # Use validation only. The previous sweep stopped at 1.2 and merely reported
    # its winner: the artifact still contained the original unsafe thresholds.
    sweep = [(evaluate(scale), scale) for scale in [0.5 + step * .025 for step in range(61)]]
    eligible = [(metrics, scale) for metrics, scale in sweep if metrics[0] >= .90 and metrics[1] <= .01 and metrics[2] <= .10]
    best_metrics, best_scale = max(eligible or sweep, key=lambda item: (item[0][0], -item[0][1], -item[0][2]))
    action = "calibrate-threshold" if best_metrics[0] >= .90 and best_metrics[1] <= .01 and best_metrics[2] <= .10 else "upgrade-model"
    if eligible:
        accuracy, far, frr = best_metrics
        for prototype in prototypes:
            prototype["threshold"] *= best_scale
    applied_scale = best_scale if eligible else 1.0
    diagnostics = {"recommendedAction": action, "thresholdSweepBestAccuracy": best_metrics[0],
                   "thresholdSweepBestScale": best_scale, "thresholdSweepBestFar": best_metrics[1],
                   "thresholdSweepBestFrr": best_metrics[2], "currentAccuracy": accuracy,
                   "appliedThresholdScale": applied_scale,
                   "explanation": "阈值通过准确率、误识率和拒识率门禁，写入候选制品" if action == "calibrate-threshold" else "阈值扫描无法同时通过准确率、误识率和拒识率门禁，保留原阈值并评测更强基模"}
    return {"artifactId": "artifact-" + uuid.uuid4().hex[:12], "algorithm": "prototype-calibration-gpu", "modelVersion": model_version, "createdUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "prototypes": prototypes, "diagnostics": diagnostics, "metrics": {"totalSamples": len(samples) + len(negative_samples), "totalPersons": len(prototypes), "trainAccuracy": evaluate(applied_scale, train)[0], "validationAccuracy": accuracy, "falseAcceptRate": far, "falseRejectRate": frr, "meanSimilaritySame": 0.0, "meanSimilarityDiff": 0.0}}

def main():
    if "--server" in sys.argv:
        for line in sys.stdin:
            if not line.strip():
                continue
            try:
                print(json.dumps(process(json.loads(line)), ensure_ascii=False), flush=True)
            except Exception as exc:
                print(json.dumps({"error": str(exc)}, ensure_ascii=False), flush=True)
        return
    return process(json.load(sys.stdin))

if __name__ == "__main__":
    try: print(json.dumps(main(), ensure_ascii=False))
    except Exception as exc: print(json.dumps({"error": str(exc)})); sys.exit(1)

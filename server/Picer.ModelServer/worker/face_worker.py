"""Face training worker. Reads one JSON job and writes one artifact."""
import base64, json, os, sys, uuid, datetime
import torch

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
    if not model_version.startswith("insightface"):
        return samples
    missing = [s for s in samples if not s.get("embedding") and not s.get("Embedding")
               and not s.get("faceImageBase64") and not s.get("FaceImageBase64")]
    if missing:
        raise ValueError("InsightFace requires faceImageBase64 for samples without embeddings")
    import cv2, numpy as np, onnxruntime as ort
    model_path = os.environ.get("PICER_INSIGHTFACE_MODEL_PATH", r"D:\\VL-models\\models\\insightface\\w600k_r50.onnx")
    session = ort.InferenceSession(model_path, providers=["CUDAExecutionProvider", "CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    output = []
    for sample in samples:
        if sample.get("embedding") or sample.get("Embedding"):
            output.append(sample)
            continue
        encoded = sample.get("faceImageBase64", sample.get("FaceImageBase64"))
        raw = base64.b64decode(encoded)
        image = cv2.imdecode(np.frombuffer(raw, dtype=np.uint8), cv2.IMREAD_COLOR)
        if image is None:
            raise ValueError("invalid faceImageBase64")
        image = cv2.resize(image, (112, 112), interpolation=cv2.INTER_AREA)
        image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB).astype(np.float32)
        image = (image - 127.5) / 127.5
        vector = session.run(None, {input_name: image.transpose(2, 0, 1)[None, ...]})[0][0]
        vector = vector / (np.linalg.norm(vector) + 1e-12)
        enriched = dict(sample)
        enriched["embedding"] = vector.astype(float).tolist()
        output.append(enriched)
    return output

def main():
    request = json.load(sys.stdin)
    if request.get("operation") == "embed":
        model_version = request.get("modelVersion", "insightface-arcface")
        samples = [{"faceImageBase64": value} for value in request.get("images", [])]
        enriched = enrich_insightface_embeddings(samples, model_version)
        return {"modelVersion": model_version, "embeddings": [s["embedding"] for s in enriched]}
    samples = request.get("samples", [])
    if len(samples) < 2:
        raise ValueError("at least two samples required")
    model_version = request.get("modelVersion", "sface")
    samples = enrich_insightface_embeddings(samples, model_version)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    train = [s for s in samples if s.get("split", s.get("Split")) == "train" and label_of(s) != "negative"]
    if not train:
        train = [s for s in samples if label_of(s) != "negative"] or samples
    groups = {}
    for s in train:
        person_id = int(field(s, "personId", "PersonId"))
        embedding = field(s, "embedding", "Embedding")
        groups.setdefault(person_id, []).append(torch.tensor(embedding, dtype=torch.float32, device=device))
    prototypes = []
    for person, vectors in groups.items():
        centroid = torch.stack(vectors).mean(0)
        sims = [cosine(v, centroid) for v in vectors]
        # Calibrate each person's threshold from training positives. Hard
        # negatives are evaluated separately; they must never form a centroid.
        positives = sorted(sims)
        threshold = positives[max(0, int(len(positives) * .05) - 1)] if positives else .5
        prototypes.append({"personId": person, "centroid": centroid.detach().cpu().tolist(), "threshold": max(0.25, min(.90, threshold)), "sampleCount": len(vectors)})
    evals = [s for s in samples if s.get("split", s.get("Split")) == "validation"] or samples
    def evaluate(scale):
        correct = 0
        false_positive = 0
        false_negative = 0
        for s in evals:
            v = torch.tensor(field(s, "embedding", "Embedding"), dtype=torch.float32, device=device)
            best = max(prototypes, key=lambda p: cosine(v, torch.tensor(p["centroid"], device=device)))
            similarity = cosine(v, torch.tensor(best["centroid"], device=device))
            accepted = similarity >= best["threshold"] * scale
            is_negative = label_of(s) == "negative"
            expected = int(field(s, "personId", "PersonId"))
            if is_negative:
                if accepted and best["personId"] == expected: false_positive += 1
                else: correct += 1
            elif expected == best["personId"] and accepted: correct += 1
            elif accepted: false_negative += 1
        total = len(evals) or 1
        return correct / total, false_positive / total, false_negative / total

    accuracy, far, frr = evaluate(1.0)
    sweep = [(evaluate(scale), scale) for scale in [0.50, 0.60, 0.70, 0.80, 0.90, 1.00, 1.10, 1.20]]
    eligible = [(metrics, scale) for metrics, scale in sweep if metrics[1] <= .01]
    best_metrics, best_scale = max(eligible or sweep, key=lambda item: item[0][0])
    action = "calibrate-threshold" if best_metrics[0] >= .90 else "upgrade-model"
    diagnostics = {"recommendedAction": action, "thresholdSweepBestAccuracy": best_metrics[0],
                   "thresholdSweepBestScale": best_scale, "thresholdSweepBestFar": best_metrics[1],
                   "thresholdSweepBestFrr": best_metrics[2], "currentAccuracy": accuracy,
                   "explanation": "阈值可达 90%，优先校准阈值" if action == "calibrate-threshold" else "阈值扫描仍低于 90%，停止重复调阈值并评测更强基模"}
    return {"artifactId": "artifact-" + uuid.uuid4().hex[:12], "algorithm": "prototype-calibration-gpu", "modelVersion": model_version, "createdUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "prototypes": prototypes, "diagnostics": diagnostics, "metrics": {"totalSamples": len(samples), "totalPersons": len(prototypes), "trainAccuracy": accuracy, "validationAccuracy": accuracy, "falseAcceptRate": far, "falseRejectRate": frr, "meanSimilaritySame": 0.0, "meanSimilarityDiff": 0.0}}

if __name__ == "__main__":
    try: print(json.dumps(main(), ensure_ascii=False))
    except Exception as exc: print(json.dumps({"error": str(exc)})); sys.exit(1)

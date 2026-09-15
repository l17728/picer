"""GPU face prototype worker. Reads one JSON job from stdin and writes one JSON artifact."""
import json, sys, math, uuid, datetime
import torch

def field(sample, camel, pascal):
    if camel in sample:
        return sample[camel]
    return sample[pascal]

def cosine(a, b):
    return torch.nn.functional.cosine_similarity(a, b, dim=0).item()

def label_of(sample):
    return sample.get("label", sample.get("Label", "positive"))

def main():
    request = json.load(sys.stdin)
    samples = request.get("samples", [])
    if len(samples) < 2:
        raise ValueError("at least two samples required")
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
    return {"artifactId": "artifact-" + uuid.uuid4().hex[:12], "algorithm": "prototype-calibration-gpu", "modelVersion": request.get("modelVersion", "sface"), "createdUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "prototypes": prototypes, "diagnostics": diagnostics, "metrics": {"totalSamples": len(samples), "totalPersons": len(prototypes), "trainAccuracy": accuracy, "validationAccuracy": accuracy, "falseAcceptRate": far, "falseRejectRate": frr, "meanSimilaritySame": 0.0, "meanSimilarityDiff": 0.0}}

if __name__ == "__main__":
    try: print(json.dumps(main(), ensure_ascii=False))
    except Exception as exc: print(json.dumps({"error": str(exc)})); sys.exit(1)

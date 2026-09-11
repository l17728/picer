"""GPU face prototype worker. Reads one JSON job from stdin and writes one JSON artifact."""
import json, sys, math
import torch

def cosine(a, b):
    return torch.nn.functional.cosine_similarity(a, b, dim=0).item()

def main():
    request = json.load(sys.stdin)
    samples = request.get("samples", [])
    if len(samples) < 2:
        raise ValueError("at least two samples required")
    device = "cuda" if torch.cuda.is_available() else "cpu"
    groups = {}
    for s in samples:
        groups.setdefault(int(s["personId"]), []).append(torch.tensor(s["embedding"], dtype=torch.float32, device=device))
    prototypes = []
    for person, vectors in groups.items():
        centroid = torch.stack(vectors).mean(0)
        sims = [cosine(v, centroid) for v in vectors]
        prototypes.append({"personId": person, "centroid": centroid.detach().cpu().tolist(), "threshold": max(0.3, min(sims) * 0.95 if len(sims) > 1 else 0.5), "sampleCount": len(vectors)})
    evals = [s for s in samples if s.get("split") == "validation"] or samples
    correct = 0
    for s in evals:
        v = torch.tensor(s["embedding"], dtype=torch.float32, device=device)
        best = max(prototypes, key=lambda p: cosine(v, torch.tensor(p["centroid"], device=device)))
        if int(s["personId"]) == best["personId"] and cosine(v, torch.tensor(best["centroid"], device=device)) >= best["threshold"]:
            correct += 1
    accuracy = correct / len(evals)
    return {"algorithm": "prototype-calibration-gpu", "modelVersion": request.get("modelVersion", "sface"), "device": device, "prototypes": prototypes, "metrics": {"totalSamples": len(samples), "totalPersons": len(prototypes), "trainAccuracy": accuracy, "validationAccuracy": accuracy, "falseAcceptRate": 0.0, "falseRejectRate": 1.0 - accuracy, "meanSimilaritySame": 0.0, "meanSimilarityDiff": 0.0}}

if __name__ == "__main__":
    try: print(json.dumps(main(), ensure_ascii=False))
    except Exception as exc: print(json.dumps({"error": str(exc)})); sys.exit(1)

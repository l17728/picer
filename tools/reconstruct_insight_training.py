"""Rebuild a usable InsightFace training artifact from the local confirmed set."""
import json, sqlite3, struct, hashlib, sys, time, urllib.request

DB = r"D:\PicerData\app-data\picer.db"
BASE = "http://127.0.0.1:8090/picer/"
KEY = open(r"D:\PicerData\secrets\model-server-api-key.txt", encoding="utf-8").read().strip()

def request(method, path, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(BASE + path, data=data, method=method,
        headers={"Authorization": "Bearer " + KEY, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as response:
        return json.load(response)

db = sqlite3.connect(DB)
rows = db.execute("""
 select f.id,f.image_id,a.person_id,f.embedding
 from detected_faces f join face_person_assignments a on a.face_id=f.id
 where f.model_version='insightface-arcface' and a.state='confirmed' and f.embedding is not null
 order by f.image_id,f.id
""").fetchall()
if len(rows) < 2:
    raise SystemExit("not enough confirmed InsightFace vectors")
samples = []
for index, (face_id, image_id, person_id, raw) in enumerate(rows):
    # deterministic image-level split prevents leakage from multi-face images
    split = "validation" if int(hashlib.sha256(str(image_id).encode()).hexdigest()[:8], 16) % 5 == 0 else "train"
    embedding = list(struct.unpack("<%df" % (len(raw) // 4), raw))
    samples.append({"faceId": face_id, "personId": person_id, "embedding": embedding,
        "split": split, "label": "positive",
        "evidence": {"source": "local-reconstruction", "groupId": "image:" + str(image_id),
                     "modelVersion": "insightface-arcface", "selectionReason": "confirmed user assignment"}})
snapshot = "reconstructed-insight-" + hashlib.sha256(json.dumps([(r[0], r[1]) for r in rows]).encode()).hexdigest()[:16]
dataset = {"snapshotId": snapshot, "sourceApp": "picer-reconstruction", "modelVersion": "insightface-arcface",
    "samples": samples, "attempt": 1, "selectedSampleCount": len(samples),
    "recipeFingerprint": "reconstructed-" + hashlib.sha256(json.dumps([(r[0], r[2]) for r in rows]).encode()).hexdigest()[:16],
    "recipeReason": "从本地已确认 InsightFace 向量重构；按图像分组防泄漏", "idempotencyKey": snapshot}
created = request("POST", "api/face-training/datasets", dataset)
dataset_id = created.get("id")
if not dataset_id:
    raise SystemExit("dataset rejected: " + json.dumps(created, ensure_ascii=False))
job = request("POST", "api/face-training/jobs", {"datasetId": dataset_id, "type": "train"})
job_id = job.get("id")
print(json.dumps({"dataset": dataset_id, "job": job_id, "samples": len(samples)}, ensure_ascii=False))
for _ in range(360):
    state = request("GET", "api/face-training/jobs/" + job_id)
    print(state.get("status"), state.get("stage"), state.get("progress"), state.get("artifactId"), flush=True)
    if state.get("status") in ("completed", "failed", "canceled"):
        break
    time.sleep(2)

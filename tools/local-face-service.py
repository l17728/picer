"""Minimal loopback face adapter for Picer.MediaAgent.

It exposes the MediaAgent face contract using the pinned YuNet/SFace runtime
assets. It is intentionally bound to 127.0.0.1; remote clients can only reach
it through IndexService -> MediaAgent.
"""
import base64, json, os, pathlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import cv2, numpy as np

ROOT = pathlib.Path(os.environ.get("PICER_RUNTIME_ROOT", r"D:\picer\third_party\runtime"))
YU = str(ROOT / "models" / "face" / "face_detection_yunet_2023mar.onnx")
SF = str(ROOT / "models" / "face" / "face_recognition_sface_2021dec.onnx")
detector = cv2.FaceDetectorYN.create(YU, "", (320, 320), 0.75, 0.3, 500)
recognizer = cv2.FaceRecognizerSF.create(SF, "")

def analyze(payload):
    requested_model = payload.get("modelVersion", "sface")
    # This development adapter is YuNet + OpenCV SFace, not InsightFace. Do
    # not echo a caller-provided label onto an embedding from another model:
    # the index treats modelVersion as a strict vector-space boundary.
    if requested_model != "sface":
        raise ValueError("model_version_not_supported_by_yunet_sface_adapter")
    path = payload.get("path")
    raw = None
    if path:
        raw = pathlib.Path(path).read_bytes()
    elif payload.get("imageBase64"):
        raw = base64.b64decode(payload["imageBase64"])
    if not raw:
        raise ValueError("path_or_imageBase64_required")
    image = cv2.imdecode(np.frombuffer(raw, np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("invalid_image")
    h, w = image.shape[:2]
    detector.setInputSize((w, h))
    _, rows = detector.detect(image)
    faces = []
    for ordinal, row in enumerate(rows if rows is not None else []):
        # YuNet output is x/y/w/h, five landmark pairs, then score.
        x, y, bw, bh = [float(v) for v in row[:4]]
        confidence = float(row[-1])
        x, y = max(0.0, x), max(0.0, y)
        bw, bh = min(bw, w - x), min(bh, h - y)
        aligned = recognizer.alignCrop(image, np.asarray(row[:5], np.float32))
        embedding = recognizer.feature(aligned).reshape(-1).astype(np.float32)
        embedding /= max(float(np.linalg.norm(embedding)), 1e-12)
        faces.append({"ordinal": ordinal, "x": x / w, "y": y / h,
                      "width": bw / w, "height": bh / h,
                      "confidence": confidence, "embedding": embedding.tolist()})
    return {"schemaVersion": "picer.face.v1", "operation": payload.get("operation", "detect"),
            "modelVersion": requested_model, "faces": faces}

class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        try:
            size = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(size))
            result = analyze(payload)
            body = json.dumps(result, ensure_ascii=False).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
        except Exception as exc:
            body = json.dumps({"error": str(exc)}).encode()
            self.send_response(400); self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *_):
        pass

ThreadingHTTPServer(("127.0.0.1", int(os.environ.get("PICER_FACE_PORT", "8092"))), Handler).serve_forever()

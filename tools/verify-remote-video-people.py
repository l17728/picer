"""Pinned, one-shot smoke check for a mutating remote video people request.

Requires credentials and a video ID from verify-remote-video-people.ps1.
Never prints authorization data, paths, face vectors, or full response bodies.
"""
import base64
import http.client
import json
import os
import ssl
from urllib.parse import urlsplit

from cryptography import x509
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat


def main():
    endpoint = urlsplit(os.environ["PICER_PROBE_ENDPOINT"])
    expected_pin = base64.b64decode(os.environ["PICER_PROBE_PIN"])
    video_id = int(os.environ["PICER_PROBE_VIDEO_ID"])
    connection = http.client.HTTPSConnection(
        endpoint.hostname,
        endpoint.port or 443,
        timeout=180,
        context=ssl._create_unverified_context(),
    )
    try:
        connection.connect()
        certificate = x509.load_der_x509_certificate(connection.sock.getpeercert(binary_form=True))
        actual_pin = certificate.public_key().public_bytes(Encoding.DER, PublicFormat.SubjectPublicKeyInfo)
        if actual_pin != expected_pin:
            raise ssl.SSLCertVerificationError("Pinned SPKI mismatch")
        route = endpoint.path.rstrip("/") + f"/api/service/v1/video-people/{video_id}"
        connection.request(
            "POST",
            route,
            headers={
                "Authorization": "Bearer " + os.environ["PICER_PROBE_TOKEN"],
                "Content-Length": "0",
            },
        )
        response = connection.getresponse()
        body = response.read()
        try:
            payload = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            payload = {}
        result = {"videoId": video_id, "httpStatus": response.status}
        if response.status >= 400:
            for key in ("error", "expected", "actual", "detail"):
                value = payload.get(key)
                if isinstance(value, str) and len(value) <= 100:
                    result[key] = value
            print(json.dumps(result, ensure_ascii=False), flush=True)
            return 1
        faces = payload.get("faces")
        result["modelVersion"] = payload.get("modelVersion")
        result["faceCount"] = len(faces) if isinstance(faces, list) else None
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return 0
    finally:
        connection.close()


if __name__ == "__main__":
    raise SystemExit(main())

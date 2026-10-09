"""Read-only real IndexService/MediaAgent regression probe. Never prints auth or names.

Credentials/pin are supplied by the launcher through environment variables.
No index writes, AI jobs, or original-media changes are performed.
"""
import base64
import http.client
import io
import gzip
import json
import os
import sqlite3
import ssl
import time
from urllib.parse import urlsplit
from cryptography import x509
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat
from PIL import Image


def main():
    endpoint = urlsplit(os.environ["PICER_PROBE_ENDPOINT"])
    pin = base64.b64decode(os.environ["PICER_PROBE_PIN"])
    token = os.environ["PICER_PROBE_TOKEN"]

    def get(route):
        for attempt in range(3):
            connection = http.client.HTTPSConnection(endpoint.hostname, endpoint.port or 443,
                timeout=30, context=ssl._create_unverified_context())
            try:
                connection.connect()
                certificate = x509.load_der_x509_certificate(connection.sock.getpeercert(binary_form=True))
                actual = certificate.public_key().public_bytes(Encoding.DER, PublicFormat.SubjectPublicKeyInfo)
                if actual != pin:
                    raise ssl.SSLCertVerificationError("Pinned SPKI mismatch")
                connection.request("GET", endpoint.path.rstrip("/") + route,
                    headers={"Authorization": "Bearer " + token, "Accept-Encoding": "gzip"})
                response = connection.getresponse()
                content = response.read()
                if response.getheader("Content-Encoding") == "gzip":
                    content = gzip.decompress(content)
                if response.status >= 500 and attempt < 2:
                    print(json.dumps({"event": "retry", "status": response.status, "attempt": attempt + 1}), flush=True)
                    time.sleep(.3)
                    continue
                return response.status, content
            except ssl.SSLCertVerificationError:
                raise
            except (OSError, http.client.HTTPException) as error:
                print(json.dumps({"event": "transport", "type": type(error).__name__, "attempt": attempt + 1}), flush=True)
                if attempt == 2:
                    raise
            finally:
                connection.close()

    started = time.monotonic()
    status, body = get("/api/gateway/v1/metadata?includeEmbeddings=false")
    if status != 200:
        raise RuntimeError("metadata status " + str(status))
    metadata = json.loads(body)
    keys = ("videoKeyframes", "videoFaces", "videoTranscripts", "videoSummaries")
    print(json.dumps({"event": "metadata", "bytes": len(body), "elapsedSeconds": round(time.monotonic() - started, 2),
                      "counts": {key: len(metadata.get(key) or []) for key in keys}}), flush=True)
    frames = metadata.get("videoKeyframes") or []
    checked_videos = set()
    for frame in frames:
        if frame["videoId"] in checked_videos:
            continue
        checked_videos.add(frame["videoId"])
        started = time.monotonic()
        status, data = get("/api/gateway/v1/video-keyframes/" + str(frame["id"]))
        valid = False
        if status == 200:
            with Image.open(io.BytesIO(data)) as picture:
                picture.verify()
                valid = True
        print(json.dumps({"event": "keyframe", "videoId": frame["videoId"], "frameId": frame["id"],
            "status": status, "decodable": valid, "bytes": len(data),
            "localFileExists": os.path.isfile(frame["path"]),
            "cachePath": "video-keyframes" in frame["path"],
            "outsideConfiguredRoots": b"outside configured roots" in data,
            "elapsedSeconds": round(time.monotonic() - started, 2)}), flush=True)
        if not valid:
            raise RuntimeError("keyframe not decodable")
        if len(checked_videos) == 6:
            break
    database_path = os.environ.get("PICER_PROBE_LOCAL_DB")
    if database_path:
        with sqlite3.connect("file:" + database_path.replace("\\", "/") + "?mode=ro", uri=True) as database:
            tables = ("video_keyframes", "video_detected_faces", "video_transcripts", "video_summaries")
            counts = {table: database.execute("SELECT COUNT(*) FROM " + table).fetchone()[0] for table in tables}
            print(json.dumps({"event": "localCounts", "counts": counts}), flush=True)
            comparisons = (
                ("transcripts", "SELECT video_id,transcript_json FROM video_transcripts", "videoTranscripts", "transcriptJson"),
                ("summaries", "SELECT video_id,summary FROM video_summaries", "videoSummaries", "summary"),
            )
            for label, sql, collection, field in comparisons:
                expected = dict(database.execute(sql).fetchall())
                actual = {row["videoId"]: row[field] for row in metadata.get(collection) or []}
                mismatches = sum(expected.get(key) != actual.get(key) for key in expected.keys() | actual.keys())
                print(json.dumps({"event": "contentComparison", "kind": label, "mismatches": mismatches}), flush=True)
                if label == "summaries" and mismatches:
                    print(json.dumps({
                        "event": "summaryMismatchBreakdown",
                        "localOnlyVideoIds": sorted(expected.keys() - actual.keys()),
                        "remoteOnlyVideoIds": sorted(actual.keys() - expected.keys()),
                        "differentContentVideoIds": sorted(key for key in expected.keys() & actual.keys() if expected[key] != actual[key]),
                    }), flush=True)
                if mismatches:
                    raise RuntimeError(label + " mismatch between local and remote index")
            expected_faces = set(database.execute("SELECT id,video_id,keyframe_id,assigned_person_id,assignment_state FROM video_detected_faces").fetchall())
            actual_faces = {(row["id"], row["videoId"], row["keyframeId"], row.get("assignedPersonId"), row.get("assignmentState"))
                            for row in metadata.get("videoFaces") or []}
            print(json.dumps({"event": "contentComparison", "kind": "videoFaceAssignments",
                              "mismatches": len(expected_faces ^ actual_faces)}), flush=True)
            if expected_faces != actual_faces:
                raise RuntimeError("video face assignments mismatch between local and remote index")
    print(json.dumps({"event": "complete", "checkedVideos": len(checked_videos)}), flush=True)


if __name__ == "__main__":
    main()

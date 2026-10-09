"""Read-only local ModelServer audit and bounded real GPU embedding soak.

No labels, embeddings, credentials or image paths are included in reports.
This does not certify acoustic echo cancellation or face recognition quality.
"""
import argparse
import base64
import concurrent.futures
import json
import math
from pathlib import Path
import statistics
import subprocess
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--key-file', type=Path, default=Path('D:/PicerData/secrets/model-server-api-key.txt'))
    parser.add_argument('--face-sample', type=Path)
    parser.add_argument('--seconds', type=int, default=600)
    parser.add_argument('--output', type=Path, default=Path('artifacts/p2-tests/environment.json'))
    args = parser.parse_args()
    if not 0 <= args.seconds <= 86400:
        parser.error('seconds must be between 0 and 86400')
    key = args.key_file.read_text(encoding='utf-8-sig').strip()
    report = {'generatedUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}

    def call(route, payload=None):
        data = json.dumps(payload).encode() if payload is not None else None
        request = urllib.request.Request('http://127.0.0.1:8091' + route, data=data,
            headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            return error.code, {}

    def save():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix('.tmp')
        temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        temporary.replace(args.output)

    status, data = call('/health')
    report['modelServerHealth'] = status
    status, data = call('/api/face-training/model-rankings')
    report['ranking'] = {'httpStatus': status, 'totalRounds': data.get('totalRounds'),
        'candidates': [{k: row.get(k) for k in ('modelVersion', 'rounds', 'passedRounds', 'bestValidationAccuracy', 'bestFar', 'bestFrr')}
                       for row in data.get('candidates', [])]}
    status, data = call('/api/face-training/evaluation-dataset')
    report['evaluationDataset'] = {'httpStatus': status,
        **{k: data.get(k) for k in ('sampleCount', 'persons', 'gallery', 'validation', 'test')}}
    status, data = call('/api/face-training/archive?limit=1000')
    rows = data if isinstance(data, list) else []
    completed = [row for row in rows if row.get('status') == 'completed']
    report['archive'] = {'httpStatus': status, 'rows': len(rows), 'completed': len(completed),
        'knowledgeOnly': sum(row.get('status') == 'knowledge' for row in rows),
        'bestCompletedValidationAccuracy': max((row.get('validationAccuracy') or 0 for row in completed), default=None)}
    save()
    print(json.dumps(report, ensure_ascii=False), flush=True)
    if args.face_sample is None or args.seconds == 0:
        return
    encoded = base64.b64encode(args.face_sample.read_bytes()).decode('ascii')
    payload = {'modelVersion': 'insightface-arcface', 'images': [encoded]}
    durations, gpu_memory, failures = [], [], 0
    started = time.monotonic()

    def embed():
        tick = time.monotonic()
        status, body = call('/api/face-training/embeddings', payload)
        vectors = body.get('embeddings', [])
        valid = (status == 200 and len(vectors) == 1 and len(vectors[0]) == 512
                 and all(math.isfinite(x) for x in vectors[0])
                 and sum(x*x for x in vectors[0]) > 0)
        return valid, (time.monotonic() - tick) * 1000

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        while time.monotonic() - started < args.seconds:
            for valid, duration in pool.map(lambda _: embed(), range(4)):
                durations.append(duration)
                failures += not valid
            if 'gpuRuntime' not in report:
                runtime_status, runtime = call('/api/face-training/runtime')
                report['gpuRuntime'] = runtime
                cuda = (runtime_status == 200 and runtime.get('cudaRequested') is True
                        and any('CUDAExecutionProvider' in session.get('providers', [])
                                for session in runtime.get('sessions', [])
                                if session.get('modelVersion') == payload['modelVersion']))
                if not cuda:
                    report['gpuVerification'] = 'failed: no active CUDA session'
                    save()
                    raise SystemExit('No active CUDA session; refusing to count CPU requests as GPU validation')
            usage = subprocess.run(['nvidia-smi', '--query-gpu=memory.used', '--format=csv,noheader,nounits'],
                capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
            if usage.returncode == 0:
                gpu_memory.extend(int(line.strip()) for line in usage.stdout.splitlines() if line.strip().isdigit())
            ordered = sorted(durations)
            report['gpuSoak'] = {'elapsedSeconds': round(time.monotonic()-started, 1),
                'requestedSeconds': args.seconds, 'parallelRequests': 4, 'requests': len(durations),
                'failures': failures, 'firstRequestMs': round(durations[0], 1),
                'p50Ms': round(statistics.median(durations), 1),
                'p95Ms': round(ordered[math.ceil(len(ordered)*.95)-1], 1),
                'peakDeviceMemoryMiB': max(gpu_memory, default=None), 'status': 'running'}
            save()
            print(json.dumps(report['gpuSoak']), flush=True)
            if failures >= 3:
                break
            time.sleep(min(10, max(0, args.seconds-(time.monotonic()-started))))
    report['gpuSoak']['status'] = 'passed' if failures == 0 and time.monotonic()-started >= args.seconds else 'failed'
    report['gpuSoak']['elapsedSeconds'] = round(time.monotonic()-started, 1)
    # Exercise the same worker's failure/restart path without changing any user data.
    invalid_status, _ = call('/api/face-training/embeddings', {'modelVersion': payload['modelVersion'], 'images': ['invalid-image']})
    recovered, recovery_ms = embed()
    report['gpuRecovery'] = {'invalidRequestStatus': invalid_status,
        'validRequestRecovered': recovered, 'recoveryMs': round(recovery_ms, 1)}
    save()
    if report['gpuSoak']['status'] != 'passed' or not recovered or invalid_status < 400:
        raise SystemExit(1)


if __name__ == '__main__':
    main()

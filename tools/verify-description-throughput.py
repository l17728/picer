"""Measure local VL throughput without modifying library descriptions or logging model text."""
import argparse
import base64
import concurrent.futures
import json
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
import urllib.error

parser = argparse.ArgumentParser()
parser.add_argument('--image', type=Path, required=True)
parser.add_argument('--requests', type=int, default=6)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
payload = json.dumps({'model': 'Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M', 'temperature': 0,
    'max_tokens': 128, 'messages': [{'role': 'user', 'content': [
        {'type': 'text', 'text': 'Describe the visible scene in Chinese in about 80 characters.'},
        {'type': 'image_url', 'image_url': {'url': 'data:image/jpeg;base64,' + base64.b64encode(args.image.read_bytes()).decode()}}]}]}).encode()

def request(_):
    start = time.monotonic()
    try:
        req = urllib.request.Request('http://127.0.0.1:8090/api/vl/chat', data=payload, headers={'Content-Type': 'application/json'})
        with urllib.request.urlopen(req, timeout=180) as response:
            result = json.loads(response.read())
        valid = bool(result.get('choices', [{}])[0].get('message', {}).get('content'))
        return {'seconds': time.monotonic()-start, 'success': valid, 'tokens': result.get('usage', {}).get('completion_tokens', 0)}
    except Exception as error:
        return {'seconds': time.monotonic()-start, 'success': False, 'errorType': type(error).__name__, 'status': getattr(error, 'code', None)}

stop = threading.Event()
memory = []
def monitor():
    while not stop.is_set():
        result = subprocess.run(['nvidia-smi', '--query-gpu=memory.used,utilization.gpu', '--format=csv,noheader,nounits'], capture_output=True, text=True, timeout=15)
        if result.returncode == 0:
            for row in result.stdout.splitlines():
                try: memory.append([int(part.strip()) for part in row.split(',')])
                except ValueError: pass
        stop.wait(1)

thread = threading.Thread(target=monitor, daemon=True)
thread.start()
report = {'warmup': request(0), 'runs': [], 'libraryModified': False}
deadline = time.monotonic() + 180
while not report['warmup']['success'] and time.monotonic() < deadline:
    time.sleep(3)
    report['warmup'] = request(0)
try:
    for concurrency in (1, 2, 4):
        start = time.monotonic()
        with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
            rows = list(pool.map(request, range(args.requests)))
        elapsed = time.monotonic()-start
        row = {'concurrency': concurrency, 'seconds': elapsed, 'photosPerMinute': 60*sum(r['success'] for r in rows)/elapsed,
               'failures': sum(not r['success'] for r in rows), 'requests': rows}
        report['runs'].append(row)
        print(json.dumps({k: v for k, v in row.items() if k != 'requests'}), flush=True)
finally:
    stop.set()
    thread.join(20)
    report['wholeGpuPeakMiB'] = max((row[0] for row in memory), default=None)
    report['gpuPeakUtilization'] = max((row[1] for row in memory), default=None)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
if not report['warmup']['success'] or any(run['failures'] for run in report['runs']):
    raise SystemExit(1)

"""Local short-video smoke; do not log generated descriptions or upload to a remote provider."""
import base64
import json
from pathlib import Path
import time
import urllib.request

start = time.monotonic()
data = base64.b64encode(Path('D:/VL-models/samples/fd001-validation.mp4').read_bytes()).decode()
payload = json.dumps({'model': 'Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M', 'max_tokens': 64,
    'messages': [{'role': 'user', 'content': [{'type': 'text', 'text': 'Briefly describe the visible scene.'},
        {'type': 'input_video', 'input_video': {'data': data}}]}]}).encode()
req = urllib.request.Request('http://127.0.0.1:8090/api/vl/chat', data=payload, headers={'Content-Type': 'application/json'})
try:
    with urllib.request.urlopen(req, timeout=180) as response:
        result = json.loads(response.read())
    report = {'success': bool(result.get('choices', [{}])[0].get('message', {}).get('content')), 'seconds': time.monotonic()-start}
except Exception as error:
    report = {'success': False, 'errorType': type(error).__name__, 'status': getattr(error, 'code', None), 'seconds': time.monotonic()-start}
Path('artifacts/p2-followup-tests/local-video.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report))
if not report['success']: raise SystemExit(1)

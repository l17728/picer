"""Local real-model cold/warm probes. Does not certify AEC or ASR accuracy."""
import argparse
import base64
import json
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
import urllib.error
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--restart-managed', action='store_true')
parser.add_argument('--output', type=Path, default=Path('artifacts/p2-tests/voice-services.json'))
args = parser.parse_args()
report = {'generatedUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'services':[],
    'asrComparisonPassed':False, 'aecTenMinutePassed':False,
    'limitations':['ASR smoke uses one existing English WAV, not 20 annotated real videos.',
                   'No human microphone/speaker barge-in was measured.']}
memory = []
stop = threading.Event()
def monitor():
    while not stop.is_set():
        result = subprocess.run(['nvidia-smi','--query-gpu=memory.used','--format=csv,noheader,nounits'],
            capture_output=True,text=True,timeout=15,creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode==0: memory.extend(int(s.strip()) for s in result.stdout.splitlines() if s.strip().isdigit())
        stop.wait(1)

def send(route, body=b'{}', content_type='application/json', timeout=240):
    request = urllib.request.Request('http://127.0.0.1:8090'+route, data=body,
        headers={'Content-Type':content_type})
    with urllib.request.urlopen(request,timeout=timeout) as response:
        return response.read()

sample = Path('D:/VL-models/samples/asr_en.wav').read_bytes()
boundary = 'picer-'+uuid.uuid4().hex
multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="model"\r\n\r\nqwen3-asr-1.7b\r\n'
             f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="sample.wav"\r\nContent-Type: audio/wav\r\n\r\n').encode()+sample+f'\r\n--{boundary}--\r\n'.encode()
image = base64.b64encode(Path('D:/PicerData/app-data/face-thumbs/1.v1.jpg').read_bytes()).decode()
vl = json.dumps({'model':'Qwen/Qwen3-VL-8B-Instruct-GGUF:Q4_K_M', 'messages':[{'role':'user','content':[
    {'type':'text','text':'Describe the visible colors in one short sentence.'},
    {'type':'image_url','image_url':{'url':'data:image/jpeg;base64,'+image}}]}], 'max_tokens':64}).encode()
tts = json.dumps({'model':'sherpa-onnx-vits-zh-ll', 'input':'中文语音服务验收。', 'voice':0, 'speed':1.0}).encode()
cases = [('vl','/api/vl/chat',vl,'application/json'),
         ('asr','/api/asr/transcriptions',multipart,'multipart/form-data; boundary='+boundary),
         ('tts','/api/tts/speech',tts,'application/json')]
thread = threading.Thread(target=monitor,daemon=True); thread.start()
args.output.parent.mkdir(parents=True,exist_ok=True)
try:
    for name,route,body,ctype in cases:
        row={'service':name, 'restartRequested':args.restart_managed, 'calls':[]}
        report['services'].append(row)
        started=time.monotonic()
        try:
            if args.restart_managed:
                send('/api/services/'+name+'/stop',timeout=60)
                send('/api/services/'+name+'/start',timeout=180)
            for index in range(2):
                tick=time.monotonic()
                while True:
                    try:
                        result=send(route,body,ctype)
                        break
                    except urllib.error.HTTPError as error:
                        if index != 0 or error.code not in (502,503) or time.monotonic()-tick >= 600:
                            raise
                        time.sleep(5)
                if name=='tts': valid=len(result)>44 and result[:4]==b'RIFF'
                elif name=='asr': valid=bool(json.loads(result).get('text','').strip())
                else: valid=bool(json.loads(result).get('choices',[{}])[0].get('message',{}).get('content','').strip())
                row['calls'].append({'phase':'first' if index==0 else 'warm', 'elapsedMs':round((time.monotonic()-tick)*1000,1),
                    'responseBytes':len(result),'passed':valid})
                if index==0: row['restartToFirstResultMs']=round((time.monotonic()-started)*1000,1)
            row['status']='passed' if all(c['passed'] for c in row['calls']) else 'failed'
        except Exception as error:
            row['status']='failed'; row['errorType']=type(error).__name__
            if isinstance(error, urllib.error.HTTPError): row['httpStatus']=error.code
        report['peakDeviceMemoryMiB']=max(memory,default=None)
        args.output.write_text(json.dumps(report,indent=2),encoding='utf-8')
        print(json.dumps(row),flush=True)
finally:
    stop.set(); thread.join(timeout=20)
    report['peakDeviceMemoryMiB']=max(memory,default=None)
    args.output.write_text(json.dumps(report,indent=2),encoding='utf-8')
if any(row['status']!='passed' for row in report['services']): raise SystemExit(1)

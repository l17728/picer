"""Compare local Qwen ASR and pinned Whisper against human-reviewed real video transcripts."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unicodedata
import urllib.request
import uuid

CATEGORIES = {'mandarin', 'dialect', 'quiet', 'music', 'overlap', 'silence'}

def normalized(text):
    return ''.join(c.lower() for c in unicodedata.normalize('NFKC', text) if unicodedata.category(c)[0] in 'LN')

def distance(reference, hypothesis):
    previous = list(range(len(hypothesis)+1))
    for index, expected in enumerate(reference, 1):
        current = [index]
        for column, actual in enumerate(hypothesis, 1):
            current.append(min(current[-1]+1, previous[column]+1, previous[column-1]+(expected != actual)))
        previous = current
    return previous[-1]

def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', type=Path, default=root/'artifacts/asr-evaluation/manifest.json')
    parser.add_argument('--output', type=Path, default=root/'artifacts/p2-followup-tests/asr-comparison.json')
    args = parser.parse_args()
    report = {'completed': False, 'absoluteQualityPassed': None, 'samples': []}
    def save():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k != 'samples'}))
    if not args.manifest.is_file():
        report['blockedReason'] = 'human_reviewed_video_manifest_missing'
        save(); return 2
    samples = json.loads(args.manifest.read_text(encoding='utf-8-sig'))['samples']
    if len(samples)<20 or not CATEGORIES.issubset({row['category'] for row in samples}) or not all(row.get('humanReviewed') is True and isinstance(row.get('reference'), str) for row in samples):
        report['blockedReason'] = 'at_least_20_human_reviewed_videos_covering_all_six_categories_required'
        save(); return 2
    if any(row['category'] not in CATEGORIES or (row['category'] != 'silence' and not normalized(row['reference'])) for row in samples):
        report['blockedReason'] = 'invalid_category_or_missing_non_silent_reference'
        save(); return 2
    files = [(args.manifest.parent/row['video']).resolve() for row in samples]
    if not all(path.is_file() for path in files) or len(set(files)) != len(files):
        report['blockedReason'] = 'video_files_missing_or_duplicated'
        save(); return 2
    hashes = []
    for path in files:
        with path.open('rb') as stream: hashes.append(hashlib.file_digest(stream, 'sha256').hexdigest())
    if len(set(hashes)) != len(hashes):
        report['blockedReason'] = 'duplicate_video_content'
        save(); return 2
    assets = root/'third_party/runtime'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        for index, (sample, path) in enumerate(zip(samples, files)):
            with tempfile.TemporaryDirectory(prefix='asr-', dir=args.output.parent) as temporary:
                wav = Path(temporary)/'audio.wav'
                subprocess.run([str(assets/'ffmpeg/ffmpeg.exe'), '-nostdin', '-i', str(path), '-map', '0:v:0', '-frames:v', '1', '-f', 'null', '-'], capture_output=True, check=True, timeout=120)
                subprocess.run([str(assets/'ffmpeg/ffmpeg.exe'), '-nostdin', '-y', '-i', str(path), '-vn', '-ar', '16000', '-ac', '1', str(wav)], capture_output=True, check=True, timeout=600)
                boundary = 'picer-'+uuid.uuid4().hex
                body = (f'--{boundary}\r\nContent-Disposition: form-data; name="model"\r\n\r\nqwen3-asr-1.7b\r\n--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="audio.wav"\r\nContent-Type: audio/wav\r\n\r\n').encode()+wav.read_bytes()+f'\r\n--{boundary}--\r\n'.encode()
                request = urllib.request.Request('http://127.0.0.1:8090/api/asr/transcriptions', data=body, headers={'Content-Type':'multipart/form-data; boundary='+boundary})
                with urllib.request.urlopen(request, timeout=600) as response: qwen = json.load(response)['text']
                output = Path(temporary)/'baseline'
                subprocess.run([str(assets/'whisper/whisper-cli.exe'), '-m', str(assets/'models/whisper/ggml-base-q5_1.bin'), '-f', str(wav), '-l', 'zh', '-otxt', '-of', str(output)], capture_output=True, check=True, timeout=900)
                whisper = output.with_suffix('.txt').read_text(encoding='utf-8-sig')
                reference = normalized(sample['reference'])
                report['samples'].append({'index': index, 'category': sample['category'], 'referenceCharacters': len(reference),
                    'qwenErrors': distance(reference, normalized(qwen)), 'whisperErrors': distance(reference, normalized(whisper)),
                    'qwenSilenceHallucination': not reference and bool(normalized(qwen)),
                    'whisperSilenceHallucination': not reference and bool(normalized(whisper))})
                save()
        count = sum(row['referenceCharacters'] for row in report['samples'])
        report.update(completed=True, referenceCharacters=count,
            qwenCer=sum(row['qwenErrors'] for row in report['samples'])/count if count else None,
            whisperCer=sum(row['whisperErrors'] for row in report['samples'])/count if count else None,
            note='Paired CER comparison only; no absolute quality threshold was supplied. No transcripts are logged.')
        save(); return 0
    except Exception as error:
        report['errorType'] = type(error).__name__
        save(); return 1

if __name__ == '__main__':
    raise SystemExit(main())

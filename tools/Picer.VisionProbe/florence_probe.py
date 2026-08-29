import json
import os
import sys
import time

import psutil
import torch
from PIL import Image
from transformers import AutoModelForCausalLM, AutoProcessor


def memory_mb() -> float:
    return psutil.Process().memory_info().rss / 1024 / 1024


if len(sys.argv) < 2:
    raise SystemExit("Usage: florence_probe.py <image-path> [image-path ...]")

model_id = "microsoft/Florence-2-base"
started = time.perf_counter()
processor = AutoProcessor.from_pretrained(model_id, trust_remote_code=True)
model = AutoModelForCausalLM.from_pretrained(
    model_id,
    trust_remote_code=True,
    torch_dtype=torch.float32,
).eval()
loaded = time.perf_counter()

prompt = "<DETAILED_CAPTION>"
results = []
for image_path in sys.argv[1:]:
    image_started = time.perf_counter()
    with Image.open(image_path) as source:
        image = source.convert("RGB")

    inputs = processor(text=prompt, images=image, return_tensors="pt")
    with torch.inference_mode():
        generated_ids = model.generate(
            input_ids=inputs["input_ids"],
            pixel_values=inputs["pixel_values"],
            max_new_tokens=96,
            num_beams=1,
            do_sample=False,
        )
    generated_text = processor.batch_decode(generated_ids, skip_special_tokens=False)[0]
    result = processor.post_process_generation(
        generated_text,
        task=prompt,
        image_size=image.size,
    )
    results.append({
        "image": os.path.basename(image_path),
        "inference_seconds": round(time.perf_counter() - image_started, 3),
        "result": result,
    })

print("REPORT_JSON=" + json.dumps({
    "model": model_id,
    "load_seconds": round(loaded - started, 3),
    "working_set_mb": round(memory_mb(), 1),
    "results": results,
}, ensure_ascii=False))

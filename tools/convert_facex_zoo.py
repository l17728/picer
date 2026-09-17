"""Convert the published FaceX-Zoo ResNet-IR checkpoint to ONNX."""
import argparse
import sys
from pathlib import Path

import torch


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("checkpoint", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    sys.path.insert(0, str(root))
    # The checkpoint published under the Resnet50-ir FaceX-Zoo experiment
    # contains a `backbone.*` state dict and a classification head.
    sys.path.insert(0, r"D:\VL-models\facex-zoo-src")
    from backbone.ResNets import Resnet

    checkpoint = torch.load(args.checkpoint, map_location="cpu")
    state = checkpoint.get("state_dict", checkpoint)
    backbone = {key[len("backbone.") :]: value for key, value in state.items() if key.startswith("backbone.")}
    model = Resnet(50, 0.0, "ir", 512, 7, 7)
    model.load_state_dict(backbone, strict=True)
    model.eval()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        model,
        torch.zeros(1, 3, 112, 112),
        str(args.output),
        input_names=["input"],
        output_names=["embedding"],
        dynamic_axes={"input": {0: "batch"}, "embedding": {0: "batch"}},
        opset_version=13,
    )


if __name__ == "__main__":
    main()

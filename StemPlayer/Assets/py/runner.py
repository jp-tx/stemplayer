"""Thin wrapper around audio-separator used by StemPlayer.

Prints machine-readable lines starting with '@@' on stdout; tqdm progress goes to stderr.
"""
import argparse
import json
import logging
import os
import sys


def emit(**kw):
    print("@@" + json.dumps(kw), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    ap.add_argument("--outdir", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--models-dir", required=True)
    ap.add_argument("--device", choices=["gpu", "cpu"], default="gpu")
    a = ap.parse_args()

    from audio_separator.separator import Separator

    sep = Separator(
        output_dir=a.outdir,
        model_file_dir=a.models_dir,
        output_format="WAV",
        log_level=logging.WARNING,
    )

    if a.device == "cpu":
        # Force CPU for both torch (Demucs/Roformer) and onnxruntime (MDX-Net) models.
        try:
            import torch
            sep.torch_device = torch.device("cpu")
            sep.onnx_execution_provider = ["CPUExecutionProvider"]
        except Exception as e:  # pragma: no cover
            emit(warn=f"could not force CPU: {e}")

    emit(stage="loading")
    sep.load_model(model_filename=a.model)
    emit(stage="separating")
    files = sep.separate(a.input)
    emit(done=True, files=[os.path.join(a.outdir, f) if not os.path.isabs(f) else f for f in files])


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        emit(error=str(e))
        raise

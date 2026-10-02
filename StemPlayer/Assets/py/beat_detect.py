"""Beat timestamp detection for StemPlayer, used internally (not shown in the UI yet).

Prints one machine-readable '@@' line on stdout with millisecond-accurate beat timestamps.
"""
import argparse
import json


def emit(**kw):
    print("@@" + json.dumps(kw), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    a = ap.parse_args()

    import numpy as np
    import librosa

    y, sr = librosa.load(a.input, sr=None, mono=True)
    tempo, beat_frames = librosa.beat.beat_track(y=y, sr=sr, units="frames")
    tempo = float(np.ravel(tempo)[0]) if np.ndim(tempo) else float(tempo)
    beat_times = librosa.frames_to_time(beat_frames, sr=sr)
    beats_ms = [int(round(t * 1000)) for t in beat_times]
    emit(done=True, tempo=tempo, beats_ms=beats_ms)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        emit(error=str(e))
        raise

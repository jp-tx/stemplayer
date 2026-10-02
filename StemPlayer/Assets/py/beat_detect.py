"""Beat timestamp and meter detection for StemPlayer, used internally (not shown in the UI yet).

Prints one machine-readable '@@' line on stdout with millisecond-accurate beat timestamps and a
beats-per-bar estimate (3 or 4 — 6/8 is reported as 3, matching its 3-beat feel for transcription).
"""
import argparse
import json


def emit(**kw):
    print("@@" + json.dumps(kw), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True, help="full mix, used for tempo + beat timing")
    ap.add_argument("--accent", default=None, help="optional isolated drums stem, used for meter grouping")
    a = ap.parse_args()

    import numpy as np
    import librosa

    y, sr = librosa.load(a.input, sr=None, mono=True)
    tempo, beat_frames = librosa.beat.beat_track(y=y, sr=sr, units="frames")
    tempo = float(np.ravel(tempo)[0]) if np.ndim(tempo) else float(tempo)
    beat_times = librosa.frames_to_time(beat_frames, sr=sr)
    beats_ms = [int(round(t * 1000)) for t in beat_times]

    # Meter: group beats in 3s and 4s and see which grouping makes one phase (the downbeat)
    # stand out more in the drums' accent pattern. Defaults to 4 unless 3 wins clearly.
    ay, asr = (y, sr) if not a.accent else librosa.load(a.accent, sr=sr, mono=True)
    onset_env = librosa.onset.onset_strength(y=ay, sr=asr)
    onset_times = librosa.times_like(onset_env, sr=asr)
    accents = np.interp(beat_times, onset_times, onset_env)

    def phase_contrast(n):
        if len(accents) < n * 4:
            return None
        phase_means = np.array([accents[i::n].mean() for i in range(n)])
        return (phase_means.max() - phase_means.mean()) / (phase_means.mean() + 1e-9)

    scores = {n: phase_contrast(n) for n in (3, 4)}
    beats_per_bar = 4
    meter_confidence = scores[4] or 0.0
    # Require 3 to win by a clear relative margin AND clear an absolute floor — with a weak/flat
    # accent pattern both scores sit near zero, where the ratio alone is noise-sensitive.
    min_confidence = 0.05
    if (scores[3] is not None and scores[4] is not None
            and scores[3] > scores[4] * 1.15 and scores[3] > min_confidence):
        beats_per_bar = 3
        meter_confidence = scores[3]

    emit(done=True, tempo=tempo, beats_ms=beats_ms, beats_per_bar=beats_per_bar,
         meter_confidence=float(meter_confidence))


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        emit(error=str(e))
        raise

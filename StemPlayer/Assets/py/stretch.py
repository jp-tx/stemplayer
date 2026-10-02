"""Time-stretches every stem in a track folder using pedalboard's Rubber Band-based time_stretch,
for slowed/sped-up transcription practice without changing pitch. Stems are processed in parallel
threads - pedalboard's native call releases the GIL, so this costs about as much wall-clock time as
one stem alone, not stems-count times that.
"""
import argparse
import json
import os
from concurrent.futures import ThreadPoolExecutor, as_completed


def emit(**kw):
    print("@@" + json.dumps(kw), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True, help="source track folder (the original stems)")
    ap.add_argument("--outdir", required=True, help="destination folder for the stretched stems")
    ap.add_argument("--factor", required=True, type=float, help="> 1 = faster/shorter, < 1 = slower/longer")
    a = ap.parse_args()

    import soundfile as sf
    import pedalboard

    stems = [f for f in os.listdir(a.dir) if f.lower().endswith(".wav")]
    if not stems:
        emit(error="no stems found")
        return
    os.makedirs(a.outdir, exist_ok=True)

    done = 0

    def process(name):
        nonlocal done
        y, sr = sf.read(os.path.join(a.dir, name), dtype="float32", always_2d=True)
        stretched = pedalboard.time_stretch(y.T, sr, stretch_factor=a.factor)
        sf.write(os.path.join(a.outdir, name), stretched.T, sr)
        done += 1
        emit(progress=done / len(stems))

    with ThreadPoolExecutor(max_workers=len(stems)) as ex:
        futures = [ex.submit(process, name) for name in stems]
        for f in as_completed(futures):
            f.result()  # re-raise any worker exception on the main thread

    emit(done=True, outdir=a.outdir)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        emit(error=str(e))
        raise

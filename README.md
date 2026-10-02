# StemPlayer

Cross-platform (Windows / Linux / macOS) music library that splits songs into stems on import and lets you
mix them live with faders. C# / .NET 10 / Avalonia UI; separation by
[python-audio-separator](https://github.com/nomadkaraoke/python-audio-separator); YouTube import by yt-dlp.

## Install (recommended)
Download the latest release and run the installer for your OS; it installs Python, ffmpeg, the Python
packages, and puts StemPlayer on your Desktop:
- Linux: `bash install-linux.sh`
- Windows: `powershell -ExecutionPolicy Bypass -File install-windows.ps1`

The release binaries are self-contained .NET apps; they do not bundle Python, ffmpeg or the models
(models download on first use).

## Run from source
    cd StemPlayer && dotnet run

Prerequisites: .NET 10 SDK, Python 3.10+, ffmpeg on PATH.
First launch: **Options > Set up Python environment** (creates a private venv with audio-separator + yt-dlp;
pick the CPU / NVIDIA CUDA / DirectML variant). Large download (several GB with CUDA torch).

## Features
- Import files or a YouTube link (downloaded as MP3 via yt-dlp), per-song progress bars.
- Stems stored in `~/Music/StemPlayer/<id>/*.wav` (change in Options).
- Faders: Vocals, Keys, Guitar, Bass, Everything else (drums + other), each with mute/solo; master volume; seek.
- Options: algorithm (Demucs 6-stem / 4-stem, BS-Roformer, MDX-Net, or any model filename), CPU or GPU.

- **⬆ Update** button: downloads the newest GitHub release (SHA-256 verified) over the installed executable, then offers a restart.
  Only works from the installed binary, not `dotnet run`. Updates the app only, not the Python packages.
- **Shift + drag a fader** moves every other fader by the same amount (clamped to 0-150%).

## Notes
- Only the Demucs 6-stem model separates guitar and keys; other models leave those faders disabled.
- GPU = whatever the Python packages support: NVIDIA (CUDA), AMD/Intel on Windows (DirectML variant),
  AMD on Linux (install a ROCm build of torch/onnxruntime yourself), Apple Silicon (CoreML/MPS).
- yt-dlp may warn about a missing JavaScript runtime; install `deno` (or node) if some videos fail.
- Stems are WAV (~40 MB per stem per 4 min song), so a 6-stem song is ~250 MB.

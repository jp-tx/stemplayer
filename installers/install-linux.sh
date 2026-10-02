#!/usr/bin/env bash
# StemPlayer installer for Linux (x86_64).
# Installs system dependencies (Python, ffmpeg), sets up the Python environment
# (audio-separator + yt-dlp) and puts the StemPlayer executable on your Desktop.
set -euo pipefail

REPO="jp-tx/stemplayer"
ASSET="StemPlayer-linux-x64"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DESKTOP="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/StemPlayer"   # where the app looks for its venv
VENV="$CONFIG/venv"

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

SUDO=""; [ "$(id -u)" -ne 0 ] && SUDO="sudo"

say "Installing system packages (Python 3, venv, pip, ffmpeg)"
if   command -v apt-get >/dev/null; then $SUDO apt-get update && $SUDO apt-get install -y python3 python3-venv python3-pip ffmpeg curl libfontconfig1 libx11-6 libice6 libsm6
elif command -v dnf     >/dev/null; then $SUDO dnf install -y python3 python3-pip ffmpeg-free curl fontconfig libX11 || $SUDO dnf install -y python3 python3-pip ffmpeg curl fontconfig libX11
elif command -v pacman  >/dev/null; then $SUDO pacman -Sy --needed --noconfirm python python-pip ffmpeg curl fontconfig libx11
elif command -v zypper  >/dev/null; then $SUDO zypper install -y python3 python3-pip ffmpeg curl fontconfig libX11-6
else echo "Unsupported package manager. Install Python 3.10+, python-venv and ffmpeg manually, then re-run."; exit 1; fi

say "Creating Python environment in $VENV"
mkdir -p "$CONFIG"
[ -x "$VENV/bin/python" ] || python3 -m venv "$VENV"

# NVIDIA GPU -> CUDA build; everything else -> CPU build.
# (AMD on Linux: install a ROCm build of torch/onnxruntime into this venv yourself.)
EXTRA="cpu"
if command -v nvidia-smi >/dev/null && nvidia-smi -L >/dev/null 2>&1; then EXTRA="gpu"; fi
say "Installing audio-separator[$EXTRA] and yt-dlp (large download, several GB for CUDA)"
"$VENV/bin/python" -m pip install --upgrade pip
"$VENV/bin/python" -m pip install --upgrade "audio-separator[$EXTRA]" yt-dlp audioread

say "Installing StemPlayer to $DESKTOP"
mkdir -p "$DESKTOP"
if [ -f "$HERE/$ASSET" ]; then cp "$HERE/$ASSET" "$DESKTOP/StemPlayer"
else curl -fL "https://github.com/$REPO/releases/latest/download/$ASSET" -o "$DESKTOP/StemPlayer"; fi
chmod +x "$DESKTOP/StemPlayer"

cat > "$DESKTOP/StemPlayer.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=StemPlayer
Exec=$DESKTOP/StemPlayer
Terminal=false
Categories=AudioVideo;Audio;
DESK
chmod +x "$DESKTOP/StemPlayer.desktop"
gio set "$DESKTOP/StemPlayer.desktop" metadata::trusted true 2>/dev/null || true

say "Done. Launch StemPlayer from your Desktop."

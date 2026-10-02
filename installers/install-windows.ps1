# StemPlayer installer for Windows 10/11 (x64).
# Installs Python + ffmpeg (via winget), sets up the Python environment
# (audio-separator + yt-dlp) and puts StemPlayer.exe on your Desktop.
# Run:  powershell -ExecutionPolicy Bypass -File install-windows.ps1
$ErrorActionPreference = 'Stop'

$Repo    = 'jp-tx/stemplayer'
$Asset   = 'StemPlayer-win-x64.exe'
$Native  = 'soft_oal.dll'   # OpenAL native lib; must sit beside the exe or audio playback fails
$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Desktop = [Environment]::GetFolderPath('Desktop')
$Config  = Join-Path $env:APPDATA 'StemPlayer'     # where the app looks for its venv
$Venv    = Join-Path $Config 'venv'

function Say($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget not found. Install 'App Installer' from the Microsoft Store, or install Python 3.10+ and ffmpeg manually."
}

Say 'Installing Python 3.12 and ffmpeg'
if (-not (Get-Command python -ErrorAction SilentlyContinue) -or (python --version 2>&1) -notmatch 'Python 3\.(1[0-3])') {
    winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements
}
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements
}
Refresh-Path
$Py = (Get-Command python -ErrorAction SilentlyContinue)
if (-not $Py) { throw 'Python was installed but is not on PATH yet. Open a new terminal and re-run this script.' }

Say "Creating Python environment in $Venv"
New-Item -ItemType Directory -Force -Path $Config | Out-Null
if (-not (Test-Path "$Venv\Scripts\python.exe")) { & python -m venv $Venv }
$VPy = "$Venv\Scripts\python.exe"

# NVIDIA -> CUDA build. AMD / Intel (or anything else) -> DirectML build, which uses any DirectX 12 GPU.
$Extra = 'dml'
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { $Extra = 'gpu' }
Say "Installing audio-separator[$Extra] and yt-dlp (large download)"
& $VPy -m pip install --upgrade pip
& $VPy -m pip install --upgrade "audio-separator[$Extra]" yt-dlp audioread
if ($LASTEXITCODE -ne 0) { throw 'pip install failed' }

Say "Installing StemPlayer to $Desktop"
$Dest = Join-Path $Desktop 'StemPlayer.exe'
$Local = Join-Path $Here $Asset
if (Test-Path $Local) { Copy-Item $Local $Dest -Force }
else { Invoke-WebRequest "https://github.com/$Repo/releases/latest/download/$Asset" -OutFile $Dest }

$NativeDest = Join-Path $Desktop $Native
$NativeLocal = Join-Path $Here $Native
if (Test-Path $NativeLocal) { Copy-Item $NativeLocal $NativeDest -Force }
else { Invoke-WebRequest "https://github.com/$Repo/releases/latest/download/$Native" -OutFile $NativeDest }

Say 'Done. Launch StemPlayer from your Desktop.'

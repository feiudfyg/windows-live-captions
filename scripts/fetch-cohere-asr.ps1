# Prepares the full precision Cohere Transcribe ASR backend used by the
# "cohere-py" engine (PyTorch CUDA sidecar):
#   1. creates a Python virtual environment in <repo>/data/python-env
#   2. installs PyTorch (CUDA 13), transformers etc.
#   3. downloads the official gated weights into <repo>/data/hf-cache
#
# A Hugging Face read token with access to CohereLabs/cohere-transcribe-03-2026
# must be placed in <repo>/data/hf-cache/token (one line) before running.
#
# Usage: pwsh scripts/fetch-cohere-asr.ps1 [-Python C:\Python314\python.exe] [-SkipModel]
param(
    [string]$Python = "python",
    [switch]$SkipModel
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path $root "data"
$env_dir = Join-Path $data "python-env"
$hfHome = Join-Path $data "hf-cache"
New-Item -ItemType Directory -Force -Path $env_dir, $hfHome | Out-Null

$pythonExe = Join-Path $env_dir "Scripts\python.exe"
if (-not (Test-Path $pythonExe)) {
    Write-Host "creating virtual environment in $env_dir"
    & $Python -m venv $env_dir
}

$env:PIP_CACHE_DIR = Join-Path $data "pip-cache"
$env:HF_HOME = $hfHome
$env:HF_HUB_DISABLE_XET = "1"

Write-Host "installing PyTorch (CUDA 13) ..."
& $pythonExe -m pip install torch --index-url https://download.pytorch.org/whl/cu130

Write-Host "installing transformers and audio helpers ..."
& $pythonExe -m pip install transformers soundfile numpy librosa huggingface_hub

if (-not $SkipModel) {
    $tokenPath = Join-Path $hfHome "token"
    if (-not (Test-Path $tokenPath)) {
        throw "missing Hugging Face token: $tokenPath (create a read token and accept the model terms)"
    }

    Write-Host "downloading Cohere Transcribe weights (about 3.9 GB) ..."
    & $pythonExe -c "from huggingface_hub import snapshot_download; print(snapshot_download('CohereLabs/cohere-transcribe-03-2026'))"
}

Write-Host "Cohere ASR environment ready: $pythonExe"

# Downloads the official llama.cpp nightly runtime (llama-server.exe + CUDA or
# Vulkan backends) into <repo>/data/runtime/llama.cpp. The app uses it as the
# managed translation backend, which tracks the newest model architectures
# (e.g. Qwen3.8 MTP) faster than the bundled LLamaSharp runtime.
#
# Usage: pwsh scripts/fetch-llama-server.ps1 [-Build b10956] [-Variant cuda|vulkan] [-CudaVersion 13.3]
param(
    [string]$Build = "b10956",
    [ValidateSet("cuda", "vulkan")]
    [string]$Variant = "cuda",
    [string]$CudaVersion = "13.3"
)

$ErrorActionPreference = "Stop"
# Native commands (curl, ...) must fail the script too.
if ($PSVersionTable.PSVersion -ge [Version]"7.3") {
    $PSNativeCommandUseErrorActionPreference = $true
}

$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $root "data\runtime"
$dest = Join-Path $runtime "llama.cpp"
$downloads = Join-Path $runtime "_downloads"
New-Item -ItemType Directory -Force -Path $dest, $downloads | Out-Null

$base = "https://github.com/ggml-org/llama.cpp/releases/download/$Build"

if ($Variant -eq "cuda") {
    $assets = @(
        "llama-$Build-bin-win-cuda-$CudaVersion-x64.zip",
        "cudart-llama-bin-win-cuda-$CudaVersion-x64.zip"
    )
} else {
    $assets = @("llama-$Build-bin-win-vulkan-x64.zip")
}

foreach ($asset in $assets) {
    $zip = Join-Path $downloads $asset
    if (-not (Test-Path $zip)) {
        Write-Host "downloading $asset"
        $part = "$zip.part"
        if (Test-Path $part) { Remove-Item -LiteralPath $part -Force }
        curl.exe -fL --retry 3 --retry-delay 2 -o $part "$base/$asset"
        if ($LASTEXITCODE -ne 0) {
            if (Test-Path $part) { Remove-Item -LiteralPath $part -Force }
            throw "download failed: $asset"
        }

        Move-Item -LiteralPath $part -Destination $zip -Force
    } else {
        Write-Host "using cached $asset"
    }

    Write-Host "extracting $asset"
    try {
        Expand-Archive -Path $zip -DestinationPath $dest -Force
    }
    catch {
        Write-Host "extraction failed - removing the cached archive so the next run re-downloads it"
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
        throw
    }
}

$server = Join-Path $dest "llama-server.exe"
if (-not (Test-Path $server)) {
    throw "llama-server.exe not found after extraction"
}

Write-Host "llama.cpp runtime ready: $server"

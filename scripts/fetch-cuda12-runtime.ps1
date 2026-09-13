# Downloads the CUDA 12 runtime DLLs required by the llama.cpp Cuda12 backend
# (cudart64_12.dll, cublas64_12.dll, cublasLt64_12.dll) into third_party/cuda12/x64.
#
# The CUDA toolkit installed on this machine (13.x) provides CUDA 13 runtime DLLs,
# which the Cuda12 backend cannot load (runtime libraries are major-version pinned).
#
# Usage: pwsh scripts/fetch-cuda12-runtime.ps1 [-Version 12.9.1]
param(
    [string]$Version = "12.9.1"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\cuda12"
$x64 = Join-Path $dest "x64"
$zips = Join-Path $dest "zips"
New-Item -ItemType Directory -Force -Path $x64, $zips | Out-Null

$base = "https://developer.download.nvidia.com/compute/cuda/redist"
$jsonUrl = "$base/redistrib_$Version.json"
Write-Host "fetching $jsonUrl"
$json = Invoke-RestMethod $jsonUrl

$wanted = @{}
$patterns = @("cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll")

foreach ($package in "cuda_cudart", "libcublas") {
    $entry = $json.$package."windows-x86_64"
    if (-not $entry) {
        throw "package '$package' not found in redistrib_$Version.json"
    }

    $name = Split-Path $entry.relative_path -Leaf
    $zip = Join-Path $zips $name

    if (-not (Test-Path $zip)) {
        Write-Host "downloading $name"
        curl.exe -sL -o $zip "$base/$($entry.relative_path)"
    } else {
        Write-Host "using cached $name"
    }

    $temp = Join-Path $zips "$name.tmp"
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $temp -Force

    Get-ChildItem $temp -Recurse -File |
        Where-Object { $patterns -contains $_.Name } |
        ForEach-Object {
            Write-Host "extracting $($_.Name)"
            Copy-Item $_.FullName (Join-Path $x64 $_.Name) -Force
            $wanted[$_.Name] = $true
        }

    Remove-Item $temp -Recurse -Force
}

$missing = $patterns | Where-Object { -not $wanted.ContainsKey($_) }
if ($missing) {
    throw "missing DLLs after extraction: $($missing -join ', ')"
}

Write-Host "done - CUDA 12 runtime placed in $x64"

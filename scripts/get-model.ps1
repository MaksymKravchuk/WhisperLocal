<#
.SYNOPSIS
    Downloads a Whisper model (ggml format) from HuggingFace into the models\ folder next to WhisperLocal.exe
    and verifies it.

.EXAMPLE
    .\scripts\get-model.ps1                                  # ggml-large-v3.bin into the Debug build
    .\scripts\get-model.ps1 -Configuration Release           # into the Release build
    .\scripts\get-model.ps1 ggml-large-v3-turbo.bin          # other models for comparison
    .\scripts\get-model.ps1 -ModelsDir D:\Apps\WhisperLocal\models
#>
param(
    [string]$Name = "ggml-large-v3.bin",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$ModelsDir
)

$ErrorActionPreference = "Stop"

# Known-good hashes; models without an entry are downloaded without verification
$KnownHashes = @{
    "ggml-large-v3.bin" = "64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2"
}

if (-not $ModelsDir) {
    $repo = Split-Path $PSScriptRoot -Parent
    $ModelsDir = Join-Path $repo "WhisperLocal\bin\$Configuration\net8.0-windows\models"
}
$target = Join-Path $ModelsDir $Name
$url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$Name"

New-Item -ItemType Directory -Force $ModelsDir | Out-Null

if (Test-Path $target) {
    Write-Host "Already exists: $target"
}
else {
    Write-Host "Downloading $url"
    Write-Host "         to $target"
    $partial = "$target.partial"
    # curl.exe ships with Windows 10+; -C - resumes an interrupted download
    & curl.exe -L --fail -C - -o $partial $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit code $LASTEXITCODE)" }
    Move-Item $partial $target
}

if ($KnownHashes.ContainsKey($Name)) {
    Write-Host "Verifying SHA256..."
    $hash = (Get-FileHash $target -Algorithm SHA256).Hash
    if ($hash -ne $KnownHashes[$Name]) {
        throw "SHA256 mismatch for ${Name}: got $hash, expected $($KnownHashes[$Name]). Delete the file and retry."
    }
    Write-Host "OK: $target"
}
else {
    Write-Host "Done (no known hash for $Name, not verified): $target"
}

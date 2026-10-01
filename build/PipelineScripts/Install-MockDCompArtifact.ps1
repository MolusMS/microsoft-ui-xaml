# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$ArtifactRoot,

    [Parameter(Mandatory)]
    [string]$DestinationPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSha256
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$mockDCompFiles = @(
    Get-ChildItem -LiteralPath $ArtifactRoot -Recurse -File -Filter MockDComp.dll
)
if ($mockDCompFiles.Count -ne 1) {
    $candidatePaths = @($mockDCompFiles | ForEach-Object FullName)
    throw "Expected exactly one MockDComp.dll under '$ArtifactRoot'; found $($mockDCompFiles.Count): [$($candidatePaths -join ', ')]."
}

$sourcePath = $mockDCompFiles[0].FullName
$expectedHash = $ExpectedSha256.ToUpperInvariant()
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($sourceHash -ne $expectedHash) {
    throw "MockDComp.dll hash mismatch. Expected $expectedHash, found $sourceHash at '$sourcePath'."
}

$destinationDirectory = Split-Path -Parent $DestinationPath
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    throw "MockDComp destination directory does not exist: $destinationDirectory"
}

Copy-Item -LiteralPath $sourcePath -Destination $DestinationPath -Force
$destinationHash = (Get-FileHash -LiteralPath $DestinationPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($destinationHash -ne $expectedHash) {
    throw "Overlaid MockDComp.dll hash mismatch. Expected $expectedHash, found $destinationHash at '$DestinationPath'."
}

$signature = Get-AuthenticodeSignature -LiteralPath $DestinationPath
Write-Host "Overlaid MockDComp.dll from '$sourcePath' to '$DestinationPath'."
Write-Host "MockDComp SHA-256: $destinationHash"
Write-Host "MockDComp signature status: $($signature.Status)"

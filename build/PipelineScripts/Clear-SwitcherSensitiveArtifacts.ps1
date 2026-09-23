# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactRoot,

    [string[]]$AdditionalArtifactPath = @()
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Write-Host '##vso[task.setvariable variable=SwitcherArtifactsSanitized]false'

$switcherLafToken = $env:SWITCHER_LAF_TOKEN
if ([string]::IsNullOrEmpty($switcherLafToken)) {
    throw 'SWITCHER_LAF_TOKEN must be set before sanitizing artifacts.'
}

$textExtensions = @(
    '.bat',
    '.cmd',
    '.config',
    '.csv',
    '.err',
    '.htm',
    '.html',
    '.json',
    '.log',
    '.md',
    '.out',
    '.ps1',
    '.psm1',
    '.props',
    '.targets',
    '.trx',
    '.txt',
    '.wer',
    '.xml',
    '.yaml',
    '.yml'
)
$binaryDiagnosticExtensions = @(
    '.cab',
    '.dmp',
    '.dump',
    '.etl',
    '.evtx',
    '.hdmp',
    '.mdmp',
    '.wtl',
    '.zip'
)
$redactedFileCount = 0
$removedFileCount = 0
$artifactPaths = @($ArtifactRoot) + $AdditionalArtifactPath

try {
    foreach ($path in $artifactPaths) {
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        $files = if (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -Recurse -File
        }
        else {
            Get-Item -LiteralPath $path
        }

        foreach ($file in $files) {
            if ($binaryDiagnosticExtensions -contains $file.Extension) {
                Remove-Item -LiteralPath $file.FullName -Force
                $removedFileCount++
                continue
            }

            if ($textExtensions -notcontains $file.Extension) {
                continue
            }

            $reader = [IO.StreamReader]::new(
                $file.FullName,
                [Text.UTF8Encoding]::new($false, $true),
                $true)
            try {
                $content = $reader.ReadToEnd()
                $encoding = $reader.CurrentEncoding
            }
            finally {
                $reader.Dispose()
            }
            if ($content.Contains($switcherLafToken)) {
                [IO.File]::WriteAllText(
                    $file.FullName,
                    $content.Replace($switcherLafToken, '***'),
                    $encoding)
                $redactedFileCount++
            }
        }
    }
}
catch {
    foreach ($path in $artifactPaths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    throw
}

Write-Host '##vso[task.setvariable variable=SwitcherArtifactsSanitized]true'
Write-Host (
    'Switcher artifact sanitization completed: ' +
    "redacted=$redactedFileCount removedBinaryDiagnostics=$removedFileCount.")

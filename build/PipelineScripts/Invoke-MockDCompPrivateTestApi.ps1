# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('TestPreExecutionSetup', 'TestPostExecutionCleanup')]
    [string]$EntryPoint,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$MockDCompPath,

    [Parameter(Mandatory)]
    [ValidateSet('x86', 'x64')]
    [string]$Platform
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$resolvedMockDCompPath = (Resolve-Path -LiteralPath $MockDCompPath).Path
if ($Platform -eq 'x86') {
    $rundll32Directory = if ([Environment]::Is64BitOperatingSystem) {
        'SysWOW64'
    }
    else {
        'System32'
    }
}
else {
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'An x64 MockDComp.dll cannot run on a 32-bit operating system.'
    }

    $rundll32Directory = if ([Environment]::Is64BitProcess) {
        'System32'
    }
    else {
        'Sysnative'
    }
}

$rundll32Path = Join-Path $env:WINDIR "$rundll32Directory\rundll32.exe"
if (-not (Test-Path -LiteralPath $rundll32Path -PathType Leaf)) {
    throw "The matching rundll32.exe was not found: $rundll32Path"
}

$rundll32Argument = '"{0}",{1}' -f $resolvedMockDCompPath, $EntryPoint
$process = Start-Process `
    -FilePath $rundll32Path `
    -ArgumentList $rundll32Argument `
    -WorkingDirectory (Split-Path -Parent $resolvedMockDCompPath) `
    -NoNewWindow `
    -Wait `
    -PassThru
$result = [BitConverter]::ToUInt32(
    [BitConverter]::GetBytes([int]$process.ExitCode),
    0)
Write-Host (
    "MockDComp private API {0} returned 0x{1:X8} for {2}." -f
    $EntryPoint,
    $result,
    $Platform)
if ($process.ExitCode -ne 0) {
    exit $process.ExitCode
}

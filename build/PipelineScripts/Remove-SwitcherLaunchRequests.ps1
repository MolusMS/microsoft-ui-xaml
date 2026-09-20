# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TestDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$requestDirectories = @(
    (Join-Path $TestDirectory '.winui-switcher'),
    (Join-Path $TestDirectory 'UnpackagedApps\MUXControlsTestApp\.winui-switcher'),
    (Join-Path $TestDirectory 'UnpackagedApps\TabViewTearOutApp\.winui-switcher')
)

foreach ($requestDirectory in $requestDirectories) {
    if (Test-Path -LiteralPath $requestDirectory) {
        Remove-Item -LiteralPath $requestDirectory -Recurse -Force
    }
}

Write-Host 'Removed Switcher launch request directories.'

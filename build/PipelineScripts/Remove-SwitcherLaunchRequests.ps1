# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TestDirectory,

    [string]$PackagedLaunchRequestDirectory,

    [switch]$RemoveAllPackagedLaunchRequests
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$requestDirectories = @(
    (Join-Path $TestDirectory '.winui-switcher'),
    (Join-Path $TestDirectory 'UnpackagedApps\MUXControlsTestApp\.winui-switcher'),
    (Join-Path $TestDirectory 'UnpackagedApps\TabViewTearOutApp\.winui-switcher')
)

if (-not [string]::IsNullOrWhiteSpace($PackagedLaunchRequestDirectory) -or
    $RemoveAllPackagedLaunchRequests) {
    $commonApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($commonApplicationData)) {
        throw 'The common application data directory is unavailable.'
    }
    $packagedRequestRoot = [IO.Path]::GetFullPath(
        (Join-Path `
            $commonApplicationData `
            'Microsoft\WinUI\CompositionSwitcher\Requests'))
    if ($RemoveAllPackagedLaunchRequests) {
        $requestDirectories += $packagedRequestRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($PackagedLaunchRequestDirectory)) {
        $packagedRequestPrefix =
            $packagedRequestRoot.TrimEnd(
                [IO.Path]::DirectorySeparatorChar) +
            [IO.Path]::DirectorySeparatorChar
        $packagedRequestPath = [IO.Path]::GetFullPath(
            $PackagedLaunchRequestDirectory)
        if (-not $packagedRequestPath.StartsWith(
                $packagedRequestPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $packagedRequestPath) -ne '.winui-switcher') {
            throw "Refusing to remove invalid packaged Switcher request path: $packagedRequestPath"
        }
        $requestDirectories += $packagedRequestPath
    }
}

foreach ($requestDirectory in $requestDirectories) {
    if (Test-Path -LiteralPath $requestDirectory) {
        Remove-Item -LiteralPath $requestDirectory -Recurse -Force
    }
}

Write-Host 'Removed Switcher launch request directories.'

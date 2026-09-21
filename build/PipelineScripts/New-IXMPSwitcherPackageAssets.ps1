# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AppxRecipePath,

    [Parameter(Mandatory)]
    [string]$PriConfigPath,

    [Parameter(Mandatory)]
    [string]$MakePriPath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$authorizedPackageName = 'XamlTAEFTests'
$authorizedPublisher =
    'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
$expectedApplicationId = 'taef.executionengine.universal.App'
$expectedExecutable = 'IXMPTestApp.exe'
$expectedEntryPoint = 'IXMPTestApp.App'
$manifestFileName = 'Package.Switcher.appxmanifest'

function Assert-File {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

Assert-File -Path $AppxRecipePath -Description 'IXMP AppX recipe'
Assert-File -Path $PriConfigPath -Description 'IXMP PRI configuration'
Assert-File -Path $MakePriPath -Description 'MakePri.exe'

[xml]$recipe = Get-Content -LiteralPath $AppxRecipePath -Raw
$manifestRecipeNode =
    $recipe.SelectSingleNode("//*[local-name()='AppXManifest']")
if ($null -eq $manifestRecipeNode) {
    throw "IXMP AppX recipe does not identify its generated manifest: $AppxRecipePath"
}

$generatedManifestPath = [string]$manifestRecipeNode.Include
Assert-File `
    -Path $generatedManifestPath `
    -Description 'Generated IXMP AppX manifest'

[xml]$manifest = Get-Content -LiteralPath $generatedManifestPath -Raw
$identityNode =
    $manifest.SelectSingleNode(
        "/*[local-name()='Package']/*[local-name()='Identity']")
$applicationNode =
    $manifest.SelectSingleNode(
        "/*[local-name()='Package']/*[local-name()='Applications']/" +
        "*[local-name()='Application']")
if ($null -eq $identityNode -or $null -eq $applicationNode) {
    throw "Generated IXMP manifest is missing its identity or application: $generatedManifestPath"
}

foreach ($expectedAttribute in @{
        Id = $expectedApplicationId
        Executable = $expectedExecutable
        EntryPoint = $expectedEntryPoint
    }.GetEnumerator()) {
    $actualValue = $applicationNode.GetAttribute($expectedAttribute.Key)
    if ($actualValue -ne $expectedAttribute.Value) {
        throw (
            "Generated IXMP manifest changed application $($expectedAttribute.Key). " +
            "Expected='$($expectedAttribute.Value)' Actual='$actualValue'.")
    }
}

$identityNode.SetAttribute('Name', $authorizedPackageName)
$identityNode.SetAttribute('Publisher', $authorizedPublisher)

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force |
    Out-Null

$manifestPath = Join-Path $OutputDirectory $manifestFileName
$xmlWriterSettings = [Xml.XmlWriterSettings]::new()
$xmlWriterSettings.Encoding = [Text.UTF8Encoding]::new($false)
$xmlWriterSettings.Indent = $true
$xmlWriter = [Xml.XmlWriter]::Create($manifestPath, $xmlWriterSettings)
try {
    $manifest.Save($xmlWriter)
}
finally {
    $xmlWriter.Dispose()
}

$priPath = Join-Path $OutputDirectory 'resources.pri'
& $MakePriPath new `
    /pr $OutputDirectory `
    /cf $PriConfigPath `
    /mn $manifestPath `
    /of $priPath `
    /o
if ($LASTEXITCODE -ne 0) {
    throw "MakePri.exe failed to create the Switcher IXMP PRI (exit code $LASTEXITCODE)."
}
Assert-File -Path $priPath -Description 'Switcher IXMP PRI'

$dumpPath = Join-Path $OutputDirectory 'resources.verify.xml'
try {
    & $MakePriPath dump /if $priPath /of $dumpPath /o
    if ($LASTEXITCODE -ne 0) {
        throw "MakePri.exe failed to inspect the Switcher IXMP PRI (exit code $LASTEXITCODE)."
    }
    Assert-File -Path $dumpPath -Description 'Switcher IXMP PRI verification dump'

    [xml]$dump = Get-Content -LiteralPath $dumpPath -Raw
    $resourceMap =
        $dump.SelectSingleNode(
            "/*[local-name()='PriInfo']/*[local-name()='ResourceMap']")
    if ($null -eq $resourceMap -or
        $resourceMap.GetAttribute('name') -ne $authorizedPackageName) {
        throw (
            "Switcher IXMP PRI root map is not '$authorizedPackageName'.")
    }

    foreach ($embeddedXbf in @('App.xbf', 'MainPage.xbf')) {
        $resourceNode =
            $dump.SelectSingleNode(
                "//*[local-name()='NamedResource' and @name='$embeddedXbf']")
        $embeddedCandidate =
            if ($null -ne $resourceNode) {
                $resourceNode.SelectSingleNode(
                    "*[local-name()='Candidate' and @type='EmbeddedData']")
            }
            else {
                $null
            }
        if ($null -eq $embeddedCandidate) {
            throw "Switcher IXMP PRI is missing embedded resource '$embeddedXbf'."
        }
    }

    $metadataProviderXbf =
        $dump.SelectSingleNode(
            "//*[local-name()='NamedResource' and " +
            "@name='TestAutomationHelpersPanel.xbf']")
    if ($null -eq $metadataProviderXbf) {
        throw (
            'Switcher IXMP PRI is missing the generated metadata-provider XBF.')
    }
}
finally {
    if (Test-Path -LiteralPath $dumpPath -PathType Leaf) {
        Remove-Item -LiteralPath $dumpPath -Force
    }
}

Write-Host (
    "Created Switcher IXMP package assets for " +
    "'${authorizedPackageName}_8wekyb3d8bbwe'.")

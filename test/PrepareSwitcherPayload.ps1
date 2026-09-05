# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TestPayloadDir
)

$manifestPath = Join-Path $TestPayloadDir "Test\AppXManifest.native.current.xml"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf))
{
    throw "Switcher AppX manifest not found at '$manifestPath'."
}

$dcompiPath = Join-Path (Split-Path -Parent $manifestPath) "dcompi.dll"
if (-not (Test-Path -LiteralPath $dcompiPath -PathType Leaf))
{
    throw "Switcher runtime dcompi.dll not found next to '$manifestPath'."
}

[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$namespace = $manifest.DocumentElement.NamespaceURI
$namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaceManager.AddNamespace("m", $namespace)

$classId = "Microsoft.UI.Composition.CompositionEngine"
$registeredClass = $manifest.SelectSingleNode(
    "//m:ActivatableClass[@ActivatableClassId='$classId']",
    $namespaceManager)

if ($registeredClass)
{
    $registeredPath = $registeredClass.ParentNode.SelectSingleNode("m:Path", $namespaceManager).InnerText
    if ($registeredPath -ine "dcompi.dll")
    {
        throw "$classId is already registered to '$registeredPath' instead of dcompi.dll."
    }

    Write-Host "$classId is already registered to dcompi.dll."
    return
}

$extensions = $manifest.SelectSingleNode("/m:Package/m:Extensions", $namespaceManager)
if (-not $extensions)
{
    throw "Package Extensions element not found in '$manifestPath'."
}

$server = $manifest.SelectSingleNode(
    "/m:Package/m:Extensions/m:Extension[@Category='windows.activatableClass.inProcessServer']/m:InProcessServer[m:Path='dcompi.dll']",
    $namespaceManager)

if (-not $server)
{
    $extension = $manifest.CreateElement("Extension", $namespace)
    $extension.SetAttribute("Category", "windows.activatableClass.inProcessServer")
    $server = $manifest.CreateElement("InProcessServer", $namespace)
    $path = $manifest.CreateElement("Path", $namespace)
    $path.InnerText = "dcompi.dll"
    [void]$server.AppendChild($path)
    [void]$extension.AppendChild($server)
    [void]$extensions.AppendChild($extension)
}

$activatableClass = $manifest.CreateElement("ActivatableClass", $namespace)
$activatableClass.SetAttribute("ActivatableClassId", $classId)
$activatableClass.SetAttribute("ThreadingModel", "both")
[void]$server.AppendChild($activatableClass)

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$writer = [System.Xml.XmlWriter]::Create($manifestPath, $settings)
try
{
    $manifest.Save($writer)
}
finally
{
    $writer.Dispose()
}

Write-Host "Registered $classId to dcompi.dll in '$manifestPath'."

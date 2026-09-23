# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LaunchRequestDirectory,

    [Parameter(Mandatory)]
    [string]$NativeManifestPath,

    [Parameter(Mandatory)]
    [string]$ManagedManifestPath,

    [string[]]$AdditionalLaunchRequestDirectories = @(),

    [string]$PackagedLaunchRequestManifestPath,

    [string]$RunId
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$switcherLafToken = $env:SWITCHER_LAF_TOKEN
if ([string]::IsNullOrEmpty($switcherLafToken)) {
    throw 'SWITCHER_LAF_TOKEN must be set before creating the launch request.'
}
$hasPackagedLaunchRequest =
    -not [string]::IsNullOrWhiteSpace($PackagedLaunchRequestManifestPath)
$hasRunId = -not [string]::IsNullOrWhiteSpace($RunId)
if ($hasPackagedLaunchRequest -ne $hasRunId) {
    throw 'PackagedLaunchRequestManifestPath and RunId must be provided together.'
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class AppContainerSecurity
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PackageId
    {
        public uint reserved;
        public uint processorArchitecture;
        public ulong version;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string name;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string publisher;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string resourceId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string publisherId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromId(
        ref PackageId packageId,
        ref uint packageFamilyNameLength,
        StringBuilder packageFamilyName);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    public static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out IntPtr appContainerSid);

    [DllImport("advapi32.dll")]
    public static extern IntPtr FreeSid(IntPtr sid);

    public static string GetPackageFamilyName(string name, string publisher)
    {
        var packageId = new PackageId
        {
            name = name,
            publisher = publisher
        };
        uint length = 0;
        const int ErrorInsufficientBuffer = 122;
        int result = PackageFamilyNameFromId(
            ref packageId,
            ref length,
            null);
        if (result != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(result);
        }

        var familyName = new StringBuilder((int)length);
        result = PackageFamilyNameFromId(
            ref packageId,
            ref length,
            familyName);
        if (result != 0)
        {
            throw new Win32Exception(result);
        }
        return familyName.ToString();
    }
}
'@

function Get-AppContainerIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestPath
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Package manifest was not found: $ManifestPath"
    }

    [xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
    $identity = $manifest.Package.Identity
    $packageFamilyName = [AppContainerSecurity]::GetPackageFamilyName(
        [string]$identity.Name,
        [string]$identity.Publisher)
    $appContainerSidPointer = [IntPtr]::Zero
    $deriveResult =
        [AppContainerSecurity]::DeriveAppContainerSidFromAppContainerName(
            $packageFamilyName,
            [ref]$appContainerSidPointer)
    if ($deriveResult -ne 0) {
        throw "Failed to derive an AppContainer SID ($deriveResult)."
    }
    try {
        $sid = [Security.Principal.SecurityIdentifier]::new(
            $appContainerSidPointer)
    }
    finally {
        [void][AppContainerSecurity]::FreeSid($appContainerSidPointer)
    }

    return [PSCustomObject]@{
        PackageFamilyName = $packageFamilyName
        Sid = $sid
    }
}

$appContainerSidsByValue = @{}
foreach ($manifestPath in @($NativeManifestPath, $ManagedManifestPath)) {
    $identity = Get-AppContainerIdentity -ManifestPath $manifestPath
    $appContainerSidsByValue[$identity.Sid.Value] = $identity.Sid
}

$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$inheritance =
    [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
$propagation = [Security.AccessControl.PropagationFlags]::None
$allow = [Security.AccessControl.AccessControlType]::Allow
$fullControl = [Security.AccessControl.FileSystemRights]::FullControl
$readAndExecute = [Security.AccessControl.FileSystemRights]::ReadAndExecute
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
foreach ($sid in @(
        $currentUser,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $acl.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            $fullControl,
            $inheritance,
            $propagation,
            $allow))
}
foreach ($sid in @($appContainerSidsByValue.Values)) {
    $acl.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            $readAndExecute,
            $inheritance,
            $propagation,
            $allow))
}
$requestId = [Guid]::NewGuid().ToString('D')
$requestDirectories = @($LaunchRequestDirectory) +
    @($AdditionalLaunchRequestDirectories)
foreach ($requestDirectory in $requestDirectories) {
    $parentDirectory = Split-Path -Parent $requestDirectory
    if (-not (Test-Path -LiteralPath $parentDirectory -PathType Container)) {
        throw "Switcher launch request parent directory was not found: $parentDirectory"
    }

    if (Test-Path -LiteralPath $requestDirectory) {
        Remove-Item -LiteralPath $requestDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $requestDirectory -Force |
        Out-Null
    Set-Acl -LiteralPath $requestDirectory -AclObject $acl

    [IO.File]::WriteAllText(
        (Join-Path $requestDirectory 'system-backend'),
        $switcherLafToken,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $requestDirectory 'request-id'),
        $requestId,
        [Text.UTF8Encoding]::new($false))
}

$packagedRequestCount = 0
if ($hasPackagedLaunchRequest) {
    $packagedIdentity = Get-AppContainerIdentity `
        -ManifestPath $PackagedLaunchRequestManifestPath
    $commonApplicationData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData)
    if ([string]::IsNullOrWhiteSpace($commonApplicationData)) {
        throw 'The common application data directory is unavailable.'
    }
    $packagedRequestDirectory = Join-Path `
        $commonApplicationData `
        (Join-Path `
            'Microsoft\WinUI\CompositionSwitcher\Requests' `
            (Join-Path $packagedIdentity.PackageFamilyName '.winui-switcher'))
    Write-Host (
        '##vso[task.setvariable variable=SwitcherPackagedLaunchRequestDirectory]' +
        $packagedRequestDirectory)

    $packagedRequestParent = Split-Path -Parent $packagedRequestDirectory
    New-Item -ItemType Directory -Path $packagedRequestParent -Force |
        Out-Null
    if (Test-Path -LiteralPath $packagedRequestDirectory) {
        Remove-Item -LiteralPath $packagedRequestDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $packagedRequestDirectory -Force |
        Out-Null

    $packagedDirectoryAcl = [Security.AccessControl.DirectorySecurity]::new()
    $packagedDirectoryAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @(
            $currentUser,
            [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $packagedDirectoryAcl.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                $fullControl,
                $inheritance,
                $propagation,
                $allow))
    }
    $packagedDirectoryAcl.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $packagedIdentity.Sid,
            $readAndExecute,
            $inheritance,
            $propagation,
            $allow))
    Set-Acl `
        -LiteralPath $packagedRequestDirectory `
        -AclObject $packagedDirectoryAcl

    [IO.File]::WriteAllText(
        (Join-Path $packagedRequestDirectory 'system-backend'),
        $switcherLafToken,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $packagedRequestDirectory 'request-id'),
        $requestId,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $packagedRequestDirectory 'run-id'),
        $RunId,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $packagedRequestDirectory 'expires-at'),
        [DateTimeOffset]::UtcNow.AddHours(3).ToString(
            'O',
            [Globalization.CultureInfo]::InvariantCulture),
        [Text.UTF8Encoding]::new($false))
    $packagedRequestCount = 1
}

$totalRequestCount = $requestDirectories.Count + $packagedRequestCount
Write-Host (
    'Configured {0} ACL-protected Switcher launch request director{1}.' -f
    $totalRequestCount,
    $(if ($totalRequestCount -eq 1) { 'y' } else { 'ies' }))

# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LaunchRequestDirectory,

    [Parameter(Mandatory)]
    [string]$NativeManifestPath,

    [Parameter(Mandatory)]
    [string]$ManagedManifestPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$switcherLafToken = $env:SWITCHER_LAF_TOKEN
if ([string]::IsNullOrEmpty($switcherLafToken)) {
    throw 'SWITCHER_LAF_TOKEN must be set before creating the launch request.'
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

$appContainerSidsByValue = @{}
foreach ($manifestPath in @($NativeManifestPath, $ManagedManifestPath)) {
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
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
        $appContainerSidsByValue[$sid.Value] = $sid
    }
    finally {
        [void][AppContainerSecurity]::FreeSid($appContainerSidPointer)
    }
}

New-Item -ItemType Directory -Path $LaunchRequestDirectory -Force |
    Out-Null
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
Set-Acl -LiteralPath $LaunchRequestDirectory -AclObject $acl

$launchRequestPath = Join-Path $LaunchRequestDirectory 'system-backend'
if (Test-Path -LiteralPath $launchRequestPath) {
    Remove-Item -LiteralPath $launchRequestPath -Force
}
[IO.File]::WriteAllText(
    $launchRequestPath,
    $switcherLafToken,
    [Text.UTF8Encoding]::new($false))

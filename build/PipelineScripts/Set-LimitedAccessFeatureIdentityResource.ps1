# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ExecutablePath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageFamilyName,

    [switch]$VerifyOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (-not ('LimitedAccessFeatureResource.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace LimitedAccessFeatureResource
{
    public static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr BeginUpdateResource(
            string fileName,
            bool deleteExistingResources);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool UpdateResource(
            IntPtr update,
            string type,
            string name,
            ushort language,
            byte[] data,
            uint dataSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool EndUpdateResource(
            IntPtr update,
            bool discard);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryEx(
            string fileName,
            IntPtr file,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindResource(
            IntPtr module,
            string name,
            string type);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LoadResource(
            IntPtr module,
            IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LockResource(IntPtr resourceData);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SizeofResource(
            IntPtr module,
            IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr module);
    }
}
'@
}

$resourceType = 'LimitedAccessFeature'
$resourceName = 'Identity'
$resourceData = [Text.Encoding]::Unicode.GetBytes(
    $PackageFamilyName + [char]0)

if (-not $VerifyOnly) {
    $update = [LimitedAccessFeatureResource.NativeMethods]::BeginUpdateResource(
        $ExecutablePath,
        $false)
    if ($update -eq [IntPtr]::Zero) {
        throw [ComponentModel.Win32Exception]::new(
            [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }

    try {
        if (-not [LimitedAccessFeatureResource.NativeMethods]::UpdateResource(
                $update,
                $resourceType,
                $resourceName,
                0,
                $resourceData,
                [uint32]$resourceData.Length)) {
            throw [ComponentModel.Win32Exception]::new(
                [Runtime.InteropServices.Marshal]::GetLastWin32Error())
        }

        if (-not [LimitedAccessFeatureResource.NativeMethods]::EndUpdateResource(
                $update,
                $false)) {
            $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            $update = [IntPtr]::Zero
            throw [ComponentModel.Win32Exception]::new($error)
        }
        $update = [IntPtr]::Zero
    }
    finally {
        if ($update -ne [IntPtr]::Zero) {
            [void][LimitedAccessFeatureResource.NativeMethods]::EndUpdateResource(
                $update,
                $true)
        }
    }
}

$loadLibraryAsDataFile = 0x00000002
$loadLibraryAsImageResource = 0x00000020
$module = [LimitedAccessFeatureResource.NativeMethods]::LoadLibraryEx(
    $ExecutablePath,
    [IntPtr]::Zero,
    $loadLibraryAsDataFile -bor $loadLibraryAsImageResource)
if ($module -eq [IntPtr]::Zero) {
    throw [ComponentModel.Win32Exception]::new(
        [Runtime.InteropServices.Marshal]::GetLastWin32Error())
}

try {
    $resource = [LimitedAccessFeatureResource.NativeMethods]::FindResource(
        $module,
        $resourceName,
        $resourceType)
    if ($resource -eq [IntPtr]::Zero) {
        throw [ComponentModel.Win32Exception]::new(
            [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }

    $resourceSize =
        [LimitedAccessFeatureResource.NativeMethods]::SizeofResource(
            $module,
            $resource)
    if ($resourceSize -eq 0) {
        throw 'The LimitedAccessFeature identity resource is empty.'
    }

    $resourceHandle =
        [LimitedAccessFeatureResource.NativeMethods]::LoadResource(
            $module,
            $resource)
    if ($resourceHandle -eq [IntPtr]::Zero) {
        throw [ComponentModel.Win32Exception]::new(
            [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }

    $resourcePointer =
        [LimitedAccessFeatureResource.NativeMethods]::LockResource(
            $resourceHandle)
    if ($resourcePointer -eq [IntPtr]::Zero) {
        throw [ComponentModel.Win32Exception]::new(
            [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }

    $actualData = [byte[]]::new($resourceSize)
    [Runtime.InteropServices.Marshal]::Copy(
        $resourcePointer,
        $actualData,
        0,
        $actualData.Length)
    $actualPackageFamilyName =
        [Text.Encoding]::Unicode.GetString($actualData).TrimEnd([char]0)
    if ($actualPackageFamilyName -cne $PackageFamilyName) {
        throw (
            "The LimitedAccessFeature identity resource contains '{0}', " +
            "not '{1}'." -f
            $actualPackageFamilyName,
            $PackageFamilyName)
    }
}
finally {
    [void][LimitedAccessFeatureResource.NativeMethods]::FreeLibrary($module)
}

Write-Host (
    "Verified LimitedAccessFeature identity resource '{0}' in '{1}'." -f
    $PackageFamilyName,
    $ExecutablePath)

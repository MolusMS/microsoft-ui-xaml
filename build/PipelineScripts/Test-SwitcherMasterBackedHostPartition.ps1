# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntestsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not (Test-Path -LiteralPath $RuntestsPath -PathType Leaf)) {
    throw "runtests.cmd was not found at $RuntestsPath."
}

function Get-StringHash {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$Values
    )

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Values -join "`n"))
        return ([BitConverter]::ToString(
            $sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-MasterBackedTests {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('UAP', 'WPF')]
        [string]$HostingMode,

        [string]$TestBinaryPattern = '',

        [switch]$AllowEmpty
    )

    $arguments = @(
        '*'
        '-TestsWithMasterFilesOnly'
        "-HostingMode:$HostingMode"
        '-SkipPackageUninstall'
        '/list'
    )
    if (-not [string]::IsNullOrWhiteSpace($TestBinaryPattern)) {
        $arguments += "-TestBinaryPattern:$TestBinaryPattern"
    }

    Push-Location (Split-Path -Parent $RuntestsPath)
    try {
        $output = @(& $RuntestsPath @arguments 2>&1 | ForEach-Object {
            [string]$_
        })
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw (
            'Discovery for {0} (binary={1}) exited with {2}: {3}' -f
                $HostingMode,
            $TestBinaryPattern,
                $exitCode,
                ($output -join [Environment]::NewLine))
    }

    $tests = @(
        $output |
            Where-Object { $_.StartsWith((' ' * 16)) } |
            ForEach-Object { $_.Substring(16).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    if ($tests.Count -eq 0 -and -not $AllowEmpty) {
        throw (
            "Discovery for $HostingMode (binary='$TestBinaryPattern') " +
            'returned no tests.')
    }

    return $tests
}

$nativeTestBinaryPattern = 'Test\Microsoft.UI.Xaml.Tests.External.*.dll'
$managedTestBinaryPattern = 'Test\Microsoft.UI.Xaml.Tests.Managed.*.dll'

$allUapTests = @(Get-MasterBackedTests -HostingMode UAP)
$uapTests = @(
    Get-MasterBackedTests `
        -HostingMode UAP `
        -TestBinaryPattern $nativeTestBinaryPattern
)
$unsupportedManagedUapTests = @(
    Get-MasterBackedTests `
        -HostingMode UAP `
        -TestBinaryPattern $managedTestBinaryPattern `
        -AllowEmpty
)
$wpfTests = @(Get-MasterBackedTests -HostingMode WPF)
$managedWpfTests = @(
    Get-MasterBackedTests `
        -HostingMode WPF `
        -TestBinaryPattern $managedTestBinaryPattern
)
$reconstructedUapTests = @(
    $uapTests + $unsupportedManagedUapTests |
        Sort-Object -Unique
)
$partitionedTests = @($uapTests + $wpfTests | Sort-Object -Unique)

$missingUapTests = @(
    Compare-Object -ReferenceObject $allUapTests -DifferenceObject $reconstructedUapTests |
        Where-Object SideIndicator -eq '<=' |
        ForEach-Object InputObject
)
$extraUapTests = @(
    Compare-Object -ReferenceObject $allUapTests -DifferenceObject $reconstructedUapTests |
        Where-Object SideIndicator -eq '=>' |
        ForEach-Object InputObject
)
$overlappingTests = @(
    Compare-Object -ReferenceObject $uapTests -DifferenceObject $wpfTests `
        -IncludeEqual -ExcludeDifferent |
        ForEach-Object InputObject
)

$allUapHash = Get-StringHash -Values $allUapTests
$partitionedHash = Get-StringHash -Values $partitionedTests
Write-Host (
    ('Master-backed host partition: AllUAP={0}, SupportedUAP={1}, ' +
    'ExcludedManagedUAP={2}, WPF={3}, ManagedWPF={4}, SupportedTotal={5}, ' +
    'Overlap={6}, AllUAPHash={7}, SupportedHash={8}') -f
        $allUapTests.Count,
        $uapTests.Count,
        $unsupportedManagedUapTests.Count,
        $wpfTests.Count,
        $managedWpfTests.Count,
        $partitionedTests.Count,
        $overlappingTests.Count,
        $allUapHash,
        $partitionedHash)

if ($missingUapTests.Count -ne 0 -or
    $extraUapTests.Count -ne 0 -or
    $overlappingTests.Count -ne 0) {
    $sampleMissing = @($missingUapTests | Select-Object -First 20)
    $sampleExtra = @($extraUapTests | Select-Object -First 20)
    $sampleOverlap = @($overlappingTests | Select-Object -First 20)
    throw (
        ('The supported host matrix is inconsistent. UAP family ' +
        'Missing={0} [{1}] Extra={2} [{3}] SupportedHostOverlap={4} [{5}].') -f
            $missingUapTests.Count,
            ($sampleMissing -join ', '),
            $extraUapTests.Count,
            ($sampleExtra -join ', '),
            $overlappingTests.Count,
            ($sampleOverlap -join ', '))
}

Write-Host (
    '##[section]Native UAP and canonical WPF preserve the supported ' +
    'master-backed host matrix; unsupported managed-UAP variants are excluded.')

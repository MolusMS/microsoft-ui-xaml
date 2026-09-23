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

        [switch]$ForceHostingMode
    )

    $arguments = @(
        '*'
        '-TestsWithMasterFilesOnly'
        "-HostingMode:$HostingMode"
        '-SkipPackageUninstall'
        '/list'
    )
    if ($ForceHostingMode) {
        $arguments = @(
            $arguments[0..2]
            '-forceHostingMode'
            $arguments[3..($arguments.Count - 1)]
        )
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
            'Discovery for {0} (force={1}) exited with {2}: {3}' -f
                $HostingMode,
                [bool]$ForceHostingMode,
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
    if ($tests.Count -eq 0) {
        throw "Discovery for $HostingMode (force=$ForceHostingMode) returned no tests."
    }

    return $tests
}

$forcedReference = @(Get-MasterBackedTests -HostingMode WPF -ForceHostingMode)
$uapTests = @(Get-MasterBackedTests -HostingMode UAP)
$wpfTests = @(Get-MasterBackedTests -HostingMode WPF)
$partitionedTests = @($uapTests + $wpfTests | Sort-Object -Unique)

$missingTests = @(
    Compare-Object -ReferenceObject $forcedReference -DifferenceObject $partitionedTests |
        Where-Object SideIndicator -eq '<=' |
        ForEach-Object InputObject
)
$extraTests = @(
    Compare-Object -ReferenceObject $forcedReference -DifferenceObject $partitionedTests |
        Where-Object SideIndicator -eq '=>' |
        ForEach-Object InputObject
)
$overlappingTests = @(
    Compare-Object -ReferenceObject $uapTests -DifferenceObject $wpfTests `
        -IncludeEqual -ExcludeDifferent |
        ForEach-Object InputObject
)

$forcedHash = Get-StringHash -Values $forcedReference
$partitionedHash = Get-StringHash -Values $partitionedTests
Write-Host (
    ('Master-backed host partition: Reference={0}, UAP={1}, WPF={2}, ' +
    'Union={3}, Overlap={4}, ReferenceHash={5}, UnionHash={6}') -f
        $forcedReference.Count,
        $uapTests.Count,
        $wpfTests.Count,
        $partitionedTests.Count,
        $overlappingTests.Count,
        $forcedHash,
        $partitionedHash)

if ($missingTests.Count -ne 0 -or
    $extraTests.Count -ne 0 -or
    $overlappingTests.Count -ne 0) {
    $sampleMissing = @($missingTests | Select-Object -First 20)
    $sampleExtra = @($extraTests | Select-Object -First 20)
    $sampleOverlap = @($overlappingTests | Select-Object -First 20)
    throw (
        ('UAP and WPF do not form an exact, disjoint partition of the full ' +
        'master-backed suite. Missing={0} [{1}] Extra={2} [{3}] ' +
        'Overlap={4} [{5}]. Add the required hosting-mode probe instead of ' +
        'forcing unsupported tests into an existing host.') -f
            $missingTests.Count,
            ($sampleMissing -join ', '),
            $extraTests.Count,
            ($sampleExtra -join ', '),
            $overlappingTests.Count,
            ($sampleOverlap -join ', '))
}

Write-Host '##[section]UAP and WPF preserve complete master-backed coverage with host-correct selection.'

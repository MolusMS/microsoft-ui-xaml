[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [Parameter(Mandatory = $true)]
    [string]$UploadRoot,

    [Parameter(Mandatory = $true)]
    [string]$StopFile,

    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [int]$PollMilliseconds = 100
)

$ErrorActionPreference = 'Stop'

function Write-WatcherLog
{
    param([string]$Message)

    "$(Get-Date -Format o) $Message" |
        Out-File -LiteralPath $LogPath -Encoding utf8 -Append
}

function Copy-DumpSnapshot
{
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$DumpFile,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $temporaryPath = "$DestinationPath.$([Guid]::NewGuid().ToString('N')).partial"
    $sourceStream = $null
    $destinationStream = $null
    $copyCompleted = $false
    try
    {
        $sourceStream = [System.IO.File]::Open(
            $DumpFile.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        $destinationStream = [System.IO.File]::Open(
            $temporaryPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $sourceStream.CopyTo($destinationStream)
        $destinationStream.Flush()
        $copiedLength = $destinationStream.Length
        $copyCompleted = $true
    }
    finally
    {
        if($destinationStream)
        {
            $destinationStream.Dispose()
        }
        if($sourceStream)
        {
            $sourceStream.Dispose()
        }
        if(!$copyCompleted)
        {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }

    try
    {
        Move-Item -LiteralPath $temporaryPath -Destination $DestinationPath -Force
    }
    catch
    {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        throw
    }
    return $copiedLength
}

if(!(Test-Path -LiteralPath $UploadRoot -PathType Container))
{
    New-Item -ItemType Directory -Path $UploadRoot -Force | Out-Null
}

"Packaged-app dump watcher started at $(Get-Date -Format o)" |
    Out-File -LiteralPath $LogPath -Encoding utf8
Write-WatcherLog "Package root: $PackageRoot"
Write-WatcherLog "Upload root: $UploadRoot"

$observedPackageDirectories = @{}
$copiedFingerprints = @{}
$lastCopyErrors = @{}

function Copy-AvailableDumps
{
    $packageDirectories = @(
        Get-ChildItem -LiteralPath $PackageRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'XamlTAEFTests_*' }
    )

    foreach($packageDirectory in $packageDirectories)
    {
        if(!$observedPackageDirectories.ContainsKey($packageDirectory.FullName))
        {
            $observedPackageDirectories[$packageDirectory.FullName] = $true
            Write-WatcherLog "Observed package directory: $($packageDirectory.FullName)"
        }

        $dumpDirectory = Join-Path $packageDirectory.FullName 'AC\SwitcherCrashDumps'
        $dumpFiles = @(
            Get-ChildItem -LiteralPath $dumpDirectory -Filter *.dmp -File -ErrorAction SilentlyContinue
        )
        foreach($dumpFile in $dumpFiles)
        {
            $fingerprint = "$($dumpFile.Length):$($dumpFile.LastWriteTimeUtc.Ticks)"
            if($copiedFingerprints[$dumpFile.FullName] -eq $fingerprint)
            {
                continue
            }

            $destinationName = "$($packageDirectory.Name)-$($dumpFile.Name)"
            $destinationPath = Join-Path $UploadRoot $destinationName
            try
            {
                $copiedLength = Copy-DumpSnapshot -DumpFile $dumpFile -DestinationPath $destinationPath
                $copiedFingerprints[$dumpFile.FullName] = $fingerprint
                [void]$lastCopyErrors.Remove($dumpFile.FullName)
                Write-WatcherLog "Copied $copiedLength bytes from $($dumpFile.FullName) to $destinationPath"
            }
            catch
            {
                $errorMessage = $_.Exception.Message
                if($lastCopyErrors[$dumpFile.FullName] -ne $errorMessage)
                {
                    $lastCopyErrors[$dumpFile.FullName] = $errorMessage
                    Write-WatcherLog "Copy pending for $($dumpFile.FullName): $errorMessage"
                }
            }
        }
    }
}

while(!(Test-Path -LiteralPath $StopFile -PathType Leaf))
{
    Copy-AvailableDumps
    Start-Sleep -Milliseconds $PollMilliseconds
}

# The parent signals after TAEF exits. Keep watching briefly while crash writers flush and package
# cleanup begins.
for($i = 0; $i -lt 20; $i++)
{
    Copy-AvailableDumps
    Start-Sleep -Milliseconds $PollMilliseconds
}

Write-WatcherLog "Packaged-app dump watcher stopped."

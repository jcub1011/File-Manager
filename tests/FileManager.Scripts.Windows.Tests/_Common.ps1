<#
.SYNOPSIS
    Shared helpers for the File-Manager sync test-environment scripts.

.DESCRIPTION
    Dot-source this file from each New-*Env.ps1 script:

        . (Join-Path $PSScriptRoot '_Common.ps1')

    It provides the safe reset, file-creation, and profile-JSON helpers that
    every category script uses. Nothing here creates or deletes anything on its
    own -- the category scripts drive it.

    SAFETY: every destructive operation goes through Reset-EnvironmentRoot,
    which refuses to touch any path that is not strictly below the scripts
    directory ($BaseDir). The scripts only ever walk DOWN, never up.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# UTF-8 without BOM -- matches System.Text.Json expectations and keeps byte
# content identical across files (so duplicate-content hashes actually match).
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

<#
.SYNOPSIS
    Wipe and recreate a category's environment folder, safely.
.DESCRIPTION
    Resolves $BaseDir\$Name to a full path and REFUSES to proceed unless it is
    strictly below $BaseDir (no walking up, no touching $BaseDir itself). Then
    removes the folder (if present) and recreates it with an empty profiles\
    subfolder. Returns the absolute environment-root path.
#>
function Reset-EnvironmentRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $BaseDir,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $baseFull = [System.IO.Path]::GetFullPath($BaseDir)
    $target   = [System.IO.Path]::GetFullPath((Join-Path $baseFull $Name))

    # Down-only guard: target must live strictly inside the scripts directory.
    $prefix = $baseFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar) `
        + [System.IO.Path]::DirectorySeparatorChar
    if ($target -eq $baseFull -or -not $target.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset '$Name': resolved path '$target' is not strictly below '$baseFull'."
    }

    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $target 'profiles') -Force | Out-Null

    return $target
}

<#
.SYNOPSIS
    Create a test file (creating parent folders as needed) with exact content.
.DESCRIPTION
    Writes $Content as UTF-8 (no BOM) so files with identical text produce
    identical bytes -- and therefore identical content hashes. Optionally sets
    LastWriteTime, which the conflict-resolution environment relies on to
    distinguish newer/older source files.
#>
function New-TestFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Content,
        [datetime] $LastWriteTime
    )

    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, $script:Utf8NoBom)

    if ($PSBoundParameters.ContainsKey('LastWriteTime')) {
        (Get-Item -LiteralPath $Path).LastWriteTime = $LastWriteTime
    }
}

<#
.SYNOPSIS
    Build a valid baseline schema-v2 Profile as an ordered hashtable.
.DESCRIPTION
    Mirrors TestProfiles.Valid / DefaultPolicies in the C# test support. Callers
    customize the returned object before handing it to Write-ProfileJson.
    Sources/Targets are arrays of { Path } objects (absolute paths).
#>
function New-BaseProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]   $Id,
        [Parameter(Mandatory = $true)] [string]   $Name,
        [Parameter(Mandatory = $true)] [string[]] $Sources,
        [Parameter(Mandatory = $true)] [string[]] $Targets,
        [ValidateSet('AdditiveArchive', 'Mirror')] [string] $SyncMode = 'AdditiveArchive',
        [ValidateSet('PreserveStructure', 'Flatten')] [string] $TargetLayout = 'PreserveStructure',
        [bool] $ScanDestination = $false
    )

    $sourceConfigs = [object[]]@($Sources | ForEach-Object { [ordered]@{ Path = $_ } })
    $targetConfigs = [object[]]@($Targets | ForEach-Object { [ordered]@{ Path = $_ } })

    return [ordered]@{
        SchemaVersion   = 2
        ProfileId       = $Id
        Name            = $Name
        Active          = $true
        SyncMode        = $SyncMode
        ScanDestination = $ScanDestination
        TargetLayout    = $TargetLayout
        Triggers        = [ordered]@{
            ManualShell = $true
            Watcher     = $false
        }
        Sources         = $sourceConfigs
        Targets         = $targetConfigs
        Policies        = [ordered]@{
            ConflictResolution = 'Skip'
            OverwriteHandling  = 'StageOverwrites'
            VerificationMethod = 'XXH3-128'
            OnSuccess          = 'KeepSource'
            OnFailure          = 'AbortRestoreAndClean'
            MetadataOnConflict = 'WarnAndContinue'
        }
        Logging         = [ordered]@{
            Verbosity       = 'FailuresAndSkips'
            NotifyOnFailure = $true
        }
    }
}

<#
.SYNOPSIS
    Serialize a Profile hashtable to <EnvRoot>\profiles\<ProfileId>.json.
.DESCRIPTION
    Writes stable, readable, indented JSON as UTF-8 without BOM. The filename is
    the profile's GUID (matching ProfileStore's <guid>.json convention, which
    LoadAll reads). -InputObject (not the pipeline) keeps single-element
    Sources/Targets arrays as JSON arrays.
#>
function Write-ProfileJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $EnvRoot,
        [Parameter(Mandatory = $true)] [System.Collections.IDictionary] $Profile
    )

    $json = ConvertTo-Json -InputObject $Profile -Depth 20
    $path = Join-Path (Join-Path $EnvRoot 'profiles') "$($Profile.ProfileId).json"
    [System.IO.File]::WriteAllText($path, $json, $script:Utf8NoBom)
    return $path
}

<#
.SYNOPSIS
    Write a human-readable summary line for a freshly built environment.
#>
function Write-EnvironmentSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $EnvRoot,
        [Parameter(Mandatory = $true)] [int]    $ProfileCount
    )

    Write-Host "  [$Name] ready -> $EnvRoot ($ProfileCount profile(s) in .\profiles)"
}

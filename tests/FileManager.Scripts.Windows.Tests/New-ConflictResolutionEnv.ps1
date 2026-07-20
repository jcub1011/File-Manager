<#
.SYNOPSIS
    Build the conflict-resolution test environment.
.DESCRIPTION
    AdditiveArchive where every source file collides with an existing target
    file at the same relative path. Timestamps are set so:
      - newer.txt : source is NEWER than target
      - older.txt : source is OLDER than target
      - same.txt  : a plain collision
    This lets OverwriteIfNewer be distinguished from the other strategies.

    Emits FOUR profiles into profiles\ -- one per ConflictResolution value
    (Overwrite, OverwriteIfNewer, RenameSuffix, Skip). ProfileStore.LoadAll
    reads every *.json, so all four load from this one environment root.
    Re-running resets state.

    Environment root: .\conflict-resolution\
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$name    = 'conflict-resolution'
$envRoot = Reset-EnvironmentRoot -BaseDir $PSScriptRoot -Name $name

$sources = Join-Path $envRoot 'sources'
$targets = Join-Path $envRoot 'targets'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
New-Item -ItemType Directory -Path $targets -Force | Out-Null

$old = [datetime]'2024-01-01 08:00:00'
$new = [datetime]'2026-06-01 08:00:00'

# Collisions at identical relative paths, with contrasting timestamps.
New-TestFile -Path (Join-Path $sources 'newer.txt') -Content 'SOURCE newer content.' -LastWriteTime $new
New-TestFile -Path (Join-Path $targets 'newer.txt') -Content 'target older content.' -LastWriteTime $old

New-TestFile -Path (Join-Path $sources 'older.txt') -Content 'SOURCE older content.' -LastWriteTime $old
New-TestFile -Path (Join-Path $targets 'older.txt') -Content 'target newer content.' -LastWriteTime $new

New-TestFile -Path (Join-Path $sources 'same.txt')  -Content 'SOURCE content for a plain collision.'
New-TestFile -Path (Join-Path $targets 'same.txt')  -Content 'target content for a plain collision.'

$strategies = @(
    @{ Id = '33333333-3333-3333-3333-333333330001'; Value = 'Overwrite' }
    @{ Id = '33333333-3333-3333-3333-333333330002'; Value = 'OverwriteIfNewer' }
    @{ Id = '33333333-3333-3333-3333-333333330003'; Value = 'RenameSuffix' }
    @{ Id = '33333333-3333-3333-3333-333333330004'; Value = 'Skip' }
)

foreach ($s in $strategies) {
    $profile = New-BaseProfile `
        -Id $s.Id `
        -Name "Conflict - $($s.Value)" `
        -Sources @($sources) `
        -Targets @($targets) `
        -SyncMode 'AdditiveArchive' `
        -TargetLayout 'PreserveStructure'
    $profile.Policies.ConflictResolution = $s.Value
    Write-ProfileJson -EnvRoot $envRoot -Profile $profile | Out-Null
}

Write-EnvironmentSummary -Name $name -EnvRoot $envRoot -ProfileCount $strategies.Count

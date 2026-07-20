<#
.SYNOPSIS
    Build the Mirror test environment (dry-run preview only).
.DESCRIPTION
    Mirror always sweeps the destination. The target is pre-seeded with:
      - files that match source relative paths (present on both sides), and
      - ORPHAN files absent from the source set.
    A dry-run previews the orphans as Deleted (Mirror's distinguishing output).

    NOTE: the executor does not actually perform Mirror deletions in v1 -- this
    environment is for saving + dry-run previewing. Re-running resets state.

    Environment root: .\mirror\
      profiles\<guid>.json   one Mirror profile
      sources\               the desired file set
      targets\               matching files + orphans (previewed Deleted)
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$name    = 'mirror'
$envRoot = Reset-EnvironmentRoot -BaseDir $PSScriptRoot -Name $name

$sources = Join-Path $envRoot 'sources'
$targets = Join-Path $envRoot 'targets'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
New-Item -ItemType Directory -Path $targets -Force | Out-Null

# Source set (what the target should become).
New-TestFile -Path (Join-Path $sources 'keep.txt')          -Content 'Present in source and target.'
New-TestFile -Path (Join-Path $sources 'docs\manual.txt')   -Content 'Also present on both sides.'
New-TestFile -Path (Join-Path $sources 'fresh.txt')         -Content 'New in source; not yet in target.'

# Target: matching files (present on both sides) ...
New-TestFile -Path (Join-Path $targets 'keep.txt')          -Content 'Present in source and target.'
New-TestFile -Path (Join-Path $targets 'docs\manual.txt')   -Content 'Also present on both sides.'
# ... plus ORPHANS absent from source -> a real Mirror run would delete these.
New-TestFile -Path (Join-Path $targets 'stale-orphan.txt')          -Content 'Orphan at target root.'
New-TestFile -Path (Join-Path $targets 'old\deep-orphan.txt')       -Content 'Orphan in a subfolder.'

$profile = New-BaseProfile `
    -Id '22222222-2222-2222-2222-222222222222' `
    -Name 'Mirror - Orphan Preview' `
    -Sources @($sources) `
    -Targets @($targets) `
    -SyncMode 'Mirror' `
    -TargetLayout 'PreserveStructure'

Write-ProfileJson -EnvRoot $envRoot -Profile $profile | Out-Null
Write-EnvironmentSummary -Name $name -EnvRoot $envRoot -ProfileCount 1

<#
.SYNOPSIS
    Build the AdditiveArchive "happy path" test environment.
.DESCRIPTION
    One source -> one target, PreserveStructure, empty target so every source
    file classifies as New. Nested subfolders demonstrate structure preservation.
    Re-running resets the environment to this initial state.

    Environment root: .\additive-archive\  (a self-contained EnginePaths.Root)
      profiles\<guid>.json   one AdditiveArchive profile
      sources\               files to deliver (nested)
      targets\               empty (all deliveries are New)
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$name    = 'additive-archive'
$envRoot = Reset-EnvironmentRoot -BaseDir $PSScriptRoot -Name $name

$sources = Join-Path $envRoot 'sources'
$targets = Join-Path $envRoot 'targets'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
New-Item -ItemType Directory -Path $targets -Force | Out-Null

# Source tree with nested folders -> should be recreated under the target.
New-TestFile -Path (Join-Path $sources 'readme.txt')            -Content 'Top-level file.'
New-TestFile -Path (Join-Path $sources 'docs\guide.txt')        -Content 'A document in docs\.'
New-TestFile -Path (Join-Path $sources 'docs\images\logo.txt')  -Content 'Deeply nested file.'
New-TestFile -Path (Join-Path $sources 'data\report.csv')       -Content "id,value`n1,100`n2,200"

$profile = New-BaseProfile `
    -Id '11111111-1111-1111-1111-111111111111' `
    -Name 'AdditiveArchive - Basics' `
    -Sources @($sources) `
    -Targets @($targets) `
    -SyncMode 'AdditiveArchive' `
    -TargetLayout 'PreserveStructure'

Write-ProfileJson -EnvRoot $envRoot -Profile $profile | Out-Null
Write-EnvironmentSummary -Name $name -EnvRoot $envRoot -ProfileCount 1

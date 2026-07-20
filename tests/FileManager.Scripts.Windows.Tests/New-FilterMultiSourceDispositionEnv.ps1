<#
.SYNOPSIS
    Build the filters + multi-source + source-disposition test environment.
.DESCRIPTION
    Two sources (sourceA, sourceB) -> one target, TargetLayout=Flatten
    (many-to-one aggregation). Files are crafted to exercise the filter set:
      Include=[*.txt], ExcludeGlob=[*.tmp], MinSizeBytes, MaxDepth.
    An archive\ folder is created for the MoveToArchive disposition.

    Emits TWO profiles differing only by OnSuccess disposition:
      - MoveToArchive (ArchiveFolder -> .\archive)
      - MoveToTrash
    Disposition only runs on real execution (deferred in v1); these save +
    dry-run. Re-running resets state.

    Environment root: .\filters-multisource-disposition\
      sourceA\  sourceB\  targets\  archive\  profiles\
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$name    = 'filters-multisource-disposition'
$envRoot = Reset-EnvironmentRoot -BaseDir $PSScriptRoot -Name $name

$sourceA = Join-Path $envRoot 'sourceA'
$sourceB = Join-Path $envRoot 'sourceB'
$targets = Join-Path $envRoot 'targets'
$archive = Join-Path $envRoot 'archive'
New-Item -ItemType Directory -Path $sourceA -Force | Out-Null
New-Item -ItemType Directory -Path $sourceB -Force | Out-Null
New-Item -ItemType Directory -Path $targets -Force | Out-Null
New-Item -ItemType Directory -Path $archive -Force | Out-Null

# sourceA: a mix that exercises every filter rule.
New-TestFile -Path (Join-Path $sourceA 'keep-a.txt')                  -Content 'Included: matches *.txt and is large enough.'
New-TestFile -Path (Join-Path $sourceA 'scratch.tmp')                 -Content 'Excluded by *.tmp glob.'
New-TestFile -Path (Join-Path $sourceA 'notes.md')                    -Content 'Not included: extension is not *.txt.'
New-TestFile -Path (Join-Path $sourceA 'tiny.txt')                    -Content 'x'   # below MinSizeBytes -> filtered
New-TestFile -Path (Join-Path $sourceA 'deep\deeper\toodeep.txt')     -Content 'Too deep: pruned by MaxDepth.'

# sourceB: clean, included files (aggregate with sourceA under Flatten).
New-TestFile -Path (Join-Path $sourceB 'keep-b.txt')                  -Content 'Included from the second source.'
New-TestFile -Path (Join-Path $sourceB 'report.txt')                  -Content 'Another included file from source B.'

$filters = [ordered]@{
    Include      = [object[]]@('*.txt')
    ExcludeGlob  = [object[]]@('*.tmp')
    MinSizeBytes = 10
    MaxDepth     = 2
}

$dispositions = @(
    @{ Id = '44444444-4444-4444-4444-444444440001'; OnSuccess = 'MoveToArchive'; Archive = $archive }
    @{ Id = '44444444-4444-4444-4444-444444440002'; OnSuccess = 'MoveToTrash';   Archive = $null }
)

foreach ($d in $dispositions) {
    $profile = New-BaseProfile `
        -Id $d.Id `
        -Name "Filters + M:1 - $($d.OnSuccess)" `
        -Sources @($sourceA, $sourceB) `
        -Targets @($targets) `
        -SyncMode 'AdditiveArchive' `
        -TargetLayout 'Flatten'
    $profile.Filters = $filters
    $profile.Policies.OnSuccess = $d.OnSuccess
    if ($d.Archive) {
        $profile.Policies.ArchiveFolder = $d.Archive
    }
    Write-ProfileJson -EnvRoot $envRoot -Profile $profile | Out-Null
}

Write-EnvironmentSummary -Name $name -EnvRoot $envRoot -ProfileCount $dispositions.Count

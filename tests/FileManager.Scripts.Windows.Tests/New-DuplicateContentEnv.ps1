<#
.SYNOPSIS
    Build the duplicate-content test environment (identical hashes, different names).
.DESCRIPTION
    Exercises differently-named files whose bytes are IDENTICAL, so they share an
    XXH3-128 / SHA-256 content hash:
      - Within one source: report.txt and report-copy.txt are byte-identical.
      - Across subfolders: a\notes.txt and b\notes-2.txt are byte-identical.
      - Cross-side: source incoming\clone.txt is byte-identical to an existing
        target file with a DIFFERENT name (existing\original.txt).

    Under AdditiveArchive today (no content-hash dedupe), a dry-run should copy
    clone.txt as New and leave the differently-named target file Untouched
    (ScanDestination=true lists it). Emits TWO profiles differing only by
    VerificationMethod ("XXH3-128" and "SHA256") so both hashers see the set.

    ContentHashDedupe stays false (reserved -- true fails validation), but this
    is exactly the fixture the dedupe feature will need once it lands.
    Re-running resets state.

    Environment root: .\duplicate-content\
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_Common.ps1')

$name    = 'duplicate-content'
$envRoot = Reset-EnvironmentRoot -BaseDir $PSScriptRoot -Name $name

$sources = Join-Path $envRoot 'sources'
$targets = Join-Path $envRoot 'targets'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
New-Item -ItemType Directory -Path $targets -Force | Out-Null

# Content constants -- identical text -> identical bytes -> identical hash.
$reportBody = "Quarterly report body. Every byte here is identical across copies."
$notesBody  = "Shared notes. Same content, different file names and folders."
$cloneBody  = "This content already exists at the target under a different name."

# 1) Same content, different names, same folder.
New-TestFile -Path (Join-Path $sources 'report.txt')      -Content $reportBody
New-TestFile -Path (Join-Path $sources 'report-copy.txt') -Content $reportBody

# 2) Same content, different names, different subfolders.
New-TestFile -Path (Join-Path $sources 'a\notes.txt')     -Content $notesBody
New-TestFile -Path (Join-Path $sources 'b\notes-2.txt')   -Content $notesBody

# 3) Cross-side duplicate: source file identical to a differently-named target file.
New-TestFile -Path (Join-Path $sources 'incoming\clone.txt')    -Content $cloneBody
New-TestFile -Path (Join-Path $targets 'existing\original.txt') -Content $cloneBody

$verifications = @(
    @{ Id = '55555555-5555-5555-5555-555555550001'; Method = 'XXH3-128' }
    @{ Id = '55555555-5555-5555-5555-555555550002'; Method = 'SHA256' }
)

foreach ($v in $verifications) {
    $profile = New-BaseProfile `
        -Id $v.Id `
        -Name "Duplicate Content - $($v.Method)" `
        -Sources @($sources) `
        -Targets @($targets) `
        -SyncMode 'AdditiveArchive' `
        -TargetLayout 'PreserveStructure' `
        -ScanDestination $true
    $profile.Policies.VerificationMethod = $v.Method
    Write-ProfileJson -EnvRoot $envRoot -Profile $profile | Out-Null
}

Write-EnvironmentSummary -Name $name -EnvRoot $envRoot -ProfileCount $verifications.Count

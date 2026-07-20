<#
.SYNOPSIS
    Build (or reset) every sync test environment in one call.
.DESCRIPTION
    Runs each New-*Env.ps1 in this folder. Because each category script wipes and
    rebuilds only its own subfolder, this is safe to run repeatedly -- it returns
    all environments to their initial state.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scripts = @(
    'New-AdditiveArchiveEnv.ps1'
    'New-MirrorEnv.ps1'
    'New-ConflictResolutionEnv.ps1'
    'New-FilterMultiSourceDispositionEnv.ps1'
    'New-DuplicateContentEnv.ps1'
)

Write-Host "Building sync test environments under $PSScriptRoot ..."
foreach ($s in $scripts) {
    & (Join-Path $PSScriptRoot $s)
}
Write-Host "Done. All environments are at their initial state."

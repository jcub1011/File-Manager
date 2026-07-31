#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes the File-Manager UI and Service into a single flat folder, without debug symbols.

.DESCRIPTION
    ServiceLauncher.ResolveServiceExePath (src/FileManager.Contracts/IPC/ServiceLauncher.cs) falls back to
    Path.Combine(AppContext.BaseDirectory, "FileManager.Service.exe"), so a published install only starts
    the engine when the service exe sits directly beside the UI exe. The UI csproj's CopyServiceHostOutput
    target reproduces that layout for dev builds only -- it copies to $(OutDir), not $(PublishDir) -- so
    this script publishes both projects into one directory and asserts the side-by-side invariant.

    Both executables set PublishAot=true (see docs/decisions/0001-aot-vs-jit-for-executables.md), and
    native AOT is always self-contained, so the output needs no .NET runtime on the target machine but
    does need the MSVC C++ toolchain on the build machine for the ILC native link.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER OutputPath
    Destination folder. Defaults to <repo>/publish (gitignored).

.PARAMETER KeepExisting
    Skip cleaning OutputPath first. Useful for incremental re-publishes of one project.

.PARAMETER SkipToolchainCheck
    Skip MSVC linker detection and the Visual Studio dev-shell bootstrap.

.EXAMPLE
    ./publish.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $OutputPath = (Join-Path $PSScriptRoot 'publish'),
    [switch] $KeepExisting,
    [switch] $SkipToolchainCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = $PSScriptRoot
$serviceProject = Join-Path $repoRoot 'src/FileManager.Service/FileManager.Service.csproj'
$uiProject = Join-Path $repoRoot 'src/FileManager.UI/FileManager.UI.csproj'

# ---------------------------------------------------------------------------
# Preflight: TreeDataGrid submodule
# ---------------------------------------------------------------------------
# Same condition (and message) as the EnsureTreeDataGridSubmodule target in FileManager.UI.csproj. Checked
# here too so a fresh clone fails in a second instead of partway into a multi-minute AOT link.
$treeDataGrid = Join-Path $repoRoot 'third_party/TreeDataGrid/src/Avalonia.Controls.TreeDataGrid/Avalonia.Controls.TreeDataGrid.csproj'
if (-not (Test-Path -LiteralPath $treeDataGrid)) {
    throw 'The TreeDataGrid submodule is missing. Run: git submodule update --init --recursive'
}

# ---------------------------------------------------------------------------
# Preflight: MSVC toolchain for the native AOT link
# ---------------------------------------------------------------------------
# ILC shells out to link.exe. Its vswhere-based discovery has proven unreliable from a plain shell
# (ADR-0001), so import the Visual Studio dev shell explicitly when link.exe isn't already on PATH.
function Initialize-MsvcToolchain {
    if (Get-Command link.exe -ErrorAction Ignore) {
        Write-Host 'MSVC linker already on PATH.' -ForegroundColor DarkGray
        return
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $missingCpp = 'Native AOT publish needs the MSVC C++ linker. Install the "Desktop development with C++" workload in the Visual Studio Installer, or run this script from a Developer PowerShell. Use -SkipToolchainCheck to bypass this check.'

    if (-not (Test-Path -LiteralPath $vswhere)) { throw $missingCpp }

    $vsPath = & $vswhere -latest -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath
    if ([string]::IsNullOrWhiteSpace($vsPath)) { throw $missingCpp }

    $devShell = Join-Path $vsPath 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
    if (-not (Test-Path -LiteralPath $devShell)) { throw $missingCpp }

    Write-Host "Initializing MSVC toolchain from $vsPath" -ForegroundColor DarkGray
    Import-Module $devShell
    # -SkipAutomaticLocation keeps the current working directory; the dev shell otherwise cd's away.
    Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

    if (-not (Get-Command link.exe -ErrorAction Ignore)) { throw $missingCpp }
}

if ($SkipToolchainCheck) {
    Write-Host 'Skipping MSVC toolchain check (-SkipToolchainCheck).' -ForegroundColor Yellow
}
else {
    Initialize-MsvcToolchain
}

# ---------------------------------------------------------------------------
# Clean
# ---------------------------------------------------------------------------
if ($KeepExisting) {
    Write-Host "Reusing existing output at $OutputPath" -ForegroundColor Yellow
}
elseif (Test-Path -LiteralPath $OutputPath) {
    Write-Host "Cleaning $OutputPath" -ForegroundColor DarkGray
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $OutputPath -Force
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------
# DebugType/DebugSymbols suppress the app pdbs; the native pdbs that ship as NuGet runtime assets
# (libSkiaSharp, av_libglesv2) ignore those properties and are swept below.
#
# -o retargets PublishDir only, leaving $(OutDir) alone -- so the framework-dependent service copy that
# CopyServiceHostOutput drops into src/FileManager.UI/bin never enters the publish item set.
#
# Service first, UI second, so on any filename collision the UI's asset wins.
function Publish-Project {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Label
    )

    Write-Host ''
    Write-Host "Publishing $Label ($Configuration, $Runtime, native AOT)..." -ForegroundColor Cyan

    dotnet publish $Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        --output $OutputPath

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Label (exit code $LASTEXITCODE)."
    }
}

Publish-Project -Project $serviceProject -Label 'FileManager.Service'
Publish-Project -Project $uiProject -Label 'FileManager.UI'

# ---------------------------------------------------------------------------
# Strip debug files
# ---------------------------------------------------------------------------
$debugFiles = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -File -Include '*.pdb', '*.xml')
if ($debugFiles.Count -gt 0) {
    Write-Host ''
    Write-Host "Removing $($debugFiles.Count) debug/doc file(s)." -ForegroundColor DarkGray
    $debugFiles | Remove-Item -Force
}

# ---------------------------------------------------------------------------
# Verify the side-by-side layout ServiceLauncher depends on
# ---------------------------------------------------------------------------
foreach ($exe in 'FileManager.UI.exe', 'FileManager.Service.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputPath $exe))) {
        throw "$exe is missing from $OutputPath. The UI resolves the service beside its own exe, so both must be present."
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
$files = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -File)
$total = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host "Published to $OutputPath" -ForegroundColor Green
$files |
    Sort-Object -Property Length -Descending |
    ForEach-Object {
        $relative = $_.FullName.Substring($OutputPath.Length + 1)
        '{0,10:N0} KB  {1}' -f ($_.Length / 1KB), $relative
    } |
    Write-Host

Write-Host ('{0,10:N1} MB  {1} file(s) total' -f ($total / 1MB), $files.Count) -ForegroundColor Green

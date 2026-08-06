<#
.SYNOPSIS
    Builds NEXT-STEP and packages a release archive.

.DESCRIPTION
    Produces dist\NEXT-STEP-<version>.zip containing the add-in, its interop
    assemblies, its icons, the installer and the licence.

    This runs locally rather than in CI on purpose. Building requires the
    SolidWorks interop assemblies, which only exist on a machine with
    SolidWorks installed, and GitHub's hosted runners do not have one. A CI
    workflow that cannot compile the project would be theatre.

.PARAMETER Tag
    Also create the git tag for this version. Does not push.

.EXAMPLE
    .\tools\Make-Release.ps1
    .\tools\Make-Release.ps1 -Tag
#>
[CmdletBinding()]
param(
    [switch] $Tag
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\Peak.NextStep\Peak.NextStep.csproj'
$build = Join-Path $repo 'src\Peak.NextStep\bin\Release'
$dist = Join-Path $repo 'dist'

function Step { param($m) Write-Host "==> $m" -ForegroundColor Cyan }
function Fail { param($m) Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# SolidWorks locks the DLL while the add-in is loaded, and a build that
# silently kept the previous binary would ship the wrong bits.
if (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
    Fail 'SolidWorks is running. Close it so the build can replace the DLL.'
}

Step 'Building Release'
& dotnet build $proj -c Release -v minimal
if ($LASTEXITCODE -ne 0) { Fail "build failed (exit $LASTEXITCODE)" }

$dll = Join-Path $build 'Peak.NextStep.dll'
if (-not (Test-Path $dll)) { Fail "built DLL not found at $dll" }

$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
if (-not $version) { Fail 'could not read version from the built DLL' }
$version = ($version -split '\+')[0]   # strip any build metadata
Step "Version $version"

$stage = Join-Path $dist "NEXT-STEP-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'app') -Force | Out-Null

Step 'Staging payload'
# Explicit list rather than a wildcard: the build directory also collects
# nextstep-debug.log and PDBs, and neither belongs in a release.
$payload = @(
    'Peak.NextStep.dll',
    'SolidWorks.Interop.sldworks.dll',
    'SolidWorks.Interop.swconst.dll',
    'SolidWorks.Interop.swpublished.dll'
)
foreach ($f in $payload) {
    $src = Join-Path $build $f
    if (-not (Test-Path $src)) { Fail "missing from build output: $f" }
    Copy-Item $src (Join-Path $stage 'app')
}

$icons = Join-Path $build 'icons'
if (-not (Test-Path $icons)) { Fail 'icons missing from build output' }
Copy-Item $icons (Join-Path $stage 'app') -Recurse
$iconCount = (Get-ChildItem (Join-Path $stage 'app\icons') -Filter *.png).Count
if ($iconCount -ne 12) { Fail "expected 12 icon files, found $iconCount" }

Copy-Item (Join-Path $repo 'installer\Install-NEXT-STEP.ps1') $stage
Copy-Item (Join-Path $repo 'installer\Install.bat') $stage
Copy-Item (Join-Path $repo 'installer\Uninstall.bat') $stage
Copy-Item (Join-Path $repo 'LICENSE') $stage
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $stage
Copy-Item (Join-Path $repo 'README.md') $stage

$zip = Join-Path $dist "NEXT-STEP-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Step "Packing $zip"
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host ''
Write-Host "  $zip  ($size KB)" -ForegroundColor Green

if ($Tag) {
    $tagName = "v$version"
    $existing = & git -C $repo tag --list $tagName
    if ($existing) {
        Write-Host "  tag $tagName already exists; leaving it alone" -ForegroundColor Yellow
    } else {
        $dirty = & git -C $repo status --porcelain
        if ($dirty) { Fail 'working tree has uncommitted changes; commit before tagging' }
        & git -C $repo tag -a $tagName -m "NEXT-STEP $version"
        Write-Host "  created tag $tagName (not pushed)" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host 'Next: see RELEASING.md' -ForegroundColor Cyan

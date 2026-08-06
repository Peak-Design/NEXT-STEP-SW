<#
.SYNOPSIS
    Installs or removes the NEXT-STEP SolidWorks add-in.

.DESCRIPTION
    Copies the add-in beside its interop assemblies and icons, then registers
    it for COM. Registration writes to HKLM, so this needs administrator
    rights and self-elevates if it does not have them.

    RegAsm /codebase records the DLL's location in the registry, so the files
    must be in their final home before registering. That is why this copies
    first and registers second, and why moving the install directory
    afterwards breaks the add-in until it is re-registered.

.PARAMETER InstallDir
    Where to install. Defaults to "%ProgramFiles%\NEXT-STEP".

.PARAMETER Uninstall
    Unregister and delete the installation.

.PARAMETER Force
    Continue even if SolidWorks is running. The copy will fail if SolidWorks
    has the DLL loaded, so this only helps when it is running without the
    add-in.

.EXAMPLE
    .\Install-NEXT-STEP.ps1
    .\Install-NEXT-STEP.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'NEXT-STEP'),
    [switch] $Uninstall,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$AddInGuid = '{6f1b2a74-9c3d-4e15-9a88-2d4c7b0e5f31}'
$ProgId = 'Peak.NextStep.AddIn'
$DllName = 'Peak.NextStep.dll'

function Write-Step { param($m) Write-Host "  $m" }
function Write-Good { param($m) Write-Host "  $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  $m" -ForegroundColor Yellow }
function Write-Bad  { param($m) Write-Host "  $m" -ForegroundColor Red }

function Stop-Here { param($m) Write-Bad $m; Wait-IfInteractive; exit 1 }

function Wait-IfInteractive {
    if (-not $env:NEXTSTEP_NONINTERACTIVE) { Read-Host 'Press Enter to close' | Out-Null }
}

<#
Run RegAsm and return its exit code, capturing output to files.

Not `& $regasm ... 2>&1`: in Windows PowerShell 5.1 that wraps every stderr
line from a native executable in an ErrorRecord, and under
$ErrorActionPreference = 'Stop' the first one aborts the script. RegAsm always
writes warning RA0000 to stderr for an unsigned assembly registered with
/codebase -- which is every SolidWorks add-in -- so the redirect turns a
routine warning into a failed install that has already deleted the previous
registration. Start-Process keeps the streams outside PowerShell entirely.
#>
function Invoke-RegAsm {
    param([string] $RegAsm, [string[]] $Arguments, [ref] $Output)

    $outFile = [IO.Path]::GetTempFileName()
    $errFile = [IO.Path]::GetTempFileName()
    try {
        $p = Start-Process -FilePath $RegAsm -ArgumentList $Arguments -Wait -PassThru `
                           -NoNewWindow -RedirectStandardOutput $outFile `
                           -RedirectStandardError $errFile
        $text = @()
        foreach ($f in @($outFile, $errFile)) {
            if (Test-Path $f) { $text += (Get-Content $f -ErrorAction SilentlyContinue) }
        }
        $Output.Value = $text | Where-Object { $_ -and $_.Trim() }
        return $p.ExitCode
    } finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

<#
Delete every trace of the COM registration.

RegAsm keys InprocServer32 by ASSEMBLY VERSION, so unregistering version 0.2.0
leaves version 0.1.0's subkey behind, still naming a CodeBase path that the
upgrade has just overwritten or deleted. Those stale subkeys accumulate one per
release and are a genuinely confusing failure: the add-in appears registered,
and loads the wrong file or none at all.

Sweeping the whole CLSID tree makes install and uninstall idempotent regardless
of what any previous version left behind.
#>
function Remove-ComRegistration {
    $paths = @(
        "HKLM:\SOFTWARE\Classes\CLSID\$AddInGuid",
        "HKLM:\SOFTWARE\Classes\$ProgId"
    )
    foreach ($p in $paths) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # The add-in entries under each SolidWorks version. [ComUnregisterFunction]
    # normally removes these, but it cannot run if the DLL is already gone.
    $swRoot = 'HKLM:\SOFTWARE\SolidWorks'
    if (-not (Test-Path $swRoot)) { return }
    foreach ($k in Get-ChildItem $swRoot -ErrorAction SilentlyContinue) {
        $addin = Join-Path $k.PSPath "Addins\$AddInGuid"
        if (Test-Path $addin) { Remove-Item $addin -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Get-RegisteredVersions {
    $found = @()
    $swRoot = 'HKLM:\SOFTWARE\SolidWorks'
    if (-not (Test-Path $swRoot)) { return $found }
    foreach ($k in Get-ChildItem $swRoot -ErrorAction SilentlyContinue) {
        if ($k.PSChildName -notmatch '^SOLIDWORKS \d{4}$') { continue }
        if (Test-Path (Join-Path $k.PSPath "Addins\$AddInGuid")) { $found += $k.PSChildName }
    }
    return $found
}

# ---------------------------------------------------------------- elevation
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting administrator rights...'
    $argList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-InstallDir', ('"{0}"' -f $InstallDir)
    )
    if ($Uninstall) { $argList += '-Uninstall' }
    if ($Force)     { $argList += '-Force' }
    try {
        $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList `
                           -Verb RunAs -PassThru -Wait
        exit $p.ExitCode
    } catch {
        Write-Bad 'Administrator rights are required and were not granted.'
        exit 1
    }
}

# ------------------------------------------------------------------- checks
if ((Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) -and -not $Force) {
    Write-Bad 'SolidWorks is running. Close it and run this again.'
    Write-Step 'SolidWorks holds the add-in DLL open while loaded, so the files'
    Write-Step 'cannot be replaced until it exits.'
    Wait-IfInteractive
    exit 1
}

$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (-not (Test-Path $regasm)) {
    Stop-Here "RegAsm.exe not found at $regasm. .NET Framework 4.x is required."
}

$installedDll = Join-Path $InstallDir $DllName

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    Write-Host ''
    Write-Host 'Removing NEXT-STEP' -ForegroundColor Cyan

    if (Test-Path $installedDll) {
        Write-Step 'Unregistering...'
        $out = $null
        $code = Invoke-RegAsm $regasm @("`"$installedDll`"", '/unregister') ([ref]$out)
        if ($code -ne 0) { Write-Warn "RegAsm returned $code; cleaning up the registry directly" }
    } else {
        Write-Warn "No installation found at $InstallDir; cleaning up the registry anyway"
    }

    Remove-ComRegistration

    if (Test-Path $InstallDir) {
        Write-Step "Deleting $InstallDir"
        Remove-Item -Path $InstallDir -Recurse -Force
    }

    $left = Get-RegisteredVersions
    Write-Host ''
    if ($left.Count -eq 0) {
        Write-Good 'Removed. Restart SolidWorks if it is open.'
    } else {
        Write-Warn "Registry entries remain for: $($left -join ', ')"
    }
    Wait-IfInteractive
    exit 0
}

# ------------------------------------------------------------------ install
$payload = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $payload $DllName))) {
    Stop-Here "Add-in files not found in $payload. Run this from the extracted release archive, keeping the app folder next to this script."
}

$version = try {
    [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload $DllName)).FileVersion
} catch { 'unknown' }

Write-Host ''
Write-Host "Installing NEXT-STEP $version" -ForegroundColor Cyan
Write-Step "Target: $InstallDir"

if (Test-Path $installedDll) {
    Write-Step 'Unregistering the existing version...'
    $out = $null
    Invoke-RegAsm $regasm @("`"$installedDll`"", '/unregister') ([ref]$out) | Out-Null
}

# Clear stale entries from any earlier version before writing the new ones,
# so exactly one InprocServer32 version subkey exists afterwards.
Remove-ComRegistration

Write-Step 'Copying files...'
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $payload '*') -Destination $InstallDir -Recurse -Force

Write-Step 'Registering...'
$out = $null
$code = Invoke-RegAsm $regasm @("`"$installedDll`"", '/codebase') ([ref]$out)

# RA0000 warns that /codebase with an unsigned assembly can shadow other
# assemblies. It is expected here and is not a failure: the add-in is not
# strong-named, and /codebase is how SolidWorks finds a DLL outside the GAC.
$real = $out | Where-Object { $_ -notmatch 'RA0000' -and $_ -notmatch '^\s*$' }
if ($code -ne 0) {
    Write-Bad "Registration failed (exit $code):"
    $out | ForEach-Object { Write-Step $_ }
    Wait-IfInteractive
    exit 1
}
if ($real) { $real | ForEach-Object { Write-Step $_ } }

$found = Get-RegisteredVersions

Write-Host ''
if ($found.Count -gt 0) {
    Write-Good "Installed for: $($found -join ', ')"
    Write-Step 'Start SolidWorks, then enable it under Tools > Add-Ins if it is'
    Write-Step 'not already ticked. The Export STEP+ button appears on the'
    Write-Step 'NEXT-STEP tab for parts and assemblies.'
} else {
    # RegAsm can succeed while [ComRegisterFunction] finds nothing to write to.
    # Reporting success here would send the user to an empty Add-Ins dialog.
    Write-Bad 'Registered, but no SolidWorks installation was found to register into.'
    Write-Step 'Install SolidWorks first, then run this again.'
    Wait-IfInteractive
    exit 1
}

Wait-IfInteractive

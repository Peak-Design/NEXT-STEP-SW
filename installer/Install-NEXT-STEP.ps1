<#
.SYNOPSIS
    Installs or removes the NEXT-STEP SolidWorks add-in.

.DESCRIPTION
    Copies the add-in with its interop assemblies and its icons, then registers
    it for COM. Registration writes to HKLM, so this script needs administrator
    rights. It asks for them if it does not have them.

    RegAsm /codebase records the location of the DLL in the registry. The files
    must therefore be in their final place before registration. That is why this
    script copies first and registers second. If you move the install directory
    afterwards, the add-in stops working until you register it again.

.PARAMETER InstallDir
    Where to install. Defaults to "%ProgramFiles%\NEXT-STEP".

.PARAMETER Uninstall
    Unregister and delete the installation.

.PARAMETER Force
    Continues even when SolidWorks runs. The copy fails if SolidWorks has the
    DLL loaded, so this helps only when SolidWorks runs without the add-in.

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
Runs RegAsm and returns its exit code. The output goes to files.

Do not write `& $regasm ... 2>&1`. In Windows PowerShell 5.1 that wraps every
stderr line from a native program in an ErrorRecord. With
$ErrorActionPreference = 'Stop', the first line then stops the script.

RegAsm always writes warning RA0000 to stderr for an unsigned assembly with
/codebase, and that is every SolidWorks add-in. The redirection therefore turns
a routine warning into a failed install, after the script has already deleted
the previous registration. Start-Process keeps the streams out of PowerShell.
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
Deletes every part of the COM registration.

RegAsm names each InprocServer32 subkey after the ASSEMBLY VERSION. To
unregister version 0.2.0 therefore leaves the subkey of version 0.1.0 in place.
That subkey still names a CodeBase path that the upgrade has replaced or
deleted. One old subkey collects for each release, and the result confuses
everybody: the add-in looks registered, but it loads the wrong file, or no file.

This function removes the whole CLSID tree. Install and uninstall then give the
same result whatever an earlier version left behind.
#>
function Remove-ComRegistration {
    $paths = @(
        "HKLM:\SOFTWARE\Classes\CLSID\$AddInGuid",
        "HKLM:\SOFTWARE\Classes\$ProgId"
    )
    foreach ($p in $paths) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # The add-in entries under each SolidWorks version.
    # [ComUnregisterFunction] usually removes these, but it cannot run when the
    # DLL is already gone.
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
    Write-Bad 'SolidWorks is running. Close it and start this again.'
    Write-Step 'SolidWorks holds the add-in DLL open while the add-in is loaded.'
    Write-Step 'The files cannot change until SolidWorks closes.'
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
        if ($code -ne 0) { Write-Warn "RegAsm returned $code. Cleaning the registry directly." }
    } else {
        Write-Warn "No installation at $InstallDir. Cleaning the registry anyway."
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
        Write-Warn "Registry entries are still present for: $($left -join ', ')"
    }
    Wait-IfInteractive
    exit 0
}

# ------------------------------------------------------------------ install
$payload = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $payload $DllName))) {
    Stop-Here "No add-in files in $payload. Start this script from the extracted release archive, and keep the app folder next to it."
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

# Remove the entries of any earlier version before this script writes the new
# ones. Exactly one InprocServer32 version subkey then remains.
Remove-ComRegistration

Write-Step 'Copying files...'
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $payload '*') -Destination $InstallDir -Recurse -Force

Write-Step 'Registering...'
$out = $null
$code = Invoke-RegAsm $regasm @("`"$installedDll`"", '/codebase') ([ref]$out)

# RA0000 warns that /codebase with an unsigned assembly can hide other
# assemblies. That warning is expected here, and is not a failure. The add-in
# has no strong name, and /codebase is how SolidWorks finds a DLL outside the
# GAC.
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
    Write-Step 'Start SolidWorks. If NEXT-STEP is not already ticked, tick it'
    Write-Step 'in Tools > Add-Ins. The Export STEP+ button is on the NEXT-STEP'
    Write-Step 'tab, for parts and for assemblies.'
} else {
    # RegAsm can succeed while [ComRegisterFunction] finds no place to write.
    # A report of success here sends the user to an empty Add-Ins dialog.
    Write-Bad 'Registered, but this script found no SolidWorks installation.'
    Write-Step 'Install SolidWorks first, then start this script again.'
    Wait-IfInteractive
    exit 1
}

Wait-IfInteractive

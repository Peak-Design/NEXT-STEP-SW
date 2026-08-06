@echo off
rem Double-clickable wrapper. -ExecutionPolicy Bypass is scoped to this one
rem process, so it does not change the machine's PowerShell policy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-NEXT-STEP.ps1"

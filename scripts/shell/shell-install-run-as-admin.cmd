@echo off
rem Club: installs ClubShell 1.0.18 on this hybrid PC (docs/shell-install.md).
rem Right-click this file -> "Run as administrator". The kit is copied to C:\ClubShellKit first and runs from there:
rem right after the install the agent turns USB storage off for the whole PC and ejects the stick.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
if /i "%~dp0"=="C:\ClubShellKit\" goto run
echo Copying the kit to C:\ClubShellKit ...
if not exist C:\ClubShellKit mkdir C:\ClubShellKit
rem The folder only (no /T): (OI)(CI) grants do not apply to files, and /inheritance:r on files left them with an
rem empty DACL (access denied even for administrators). Files inside inherit from the folder; old ones are reset.
icacls C:\ClubShellKit /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /C /Q >nul || (echo Cannot lock C:\ClubShellKit to administrators. & pause & exit /b 1)
icacls C:\ClubShellKit\* /reset /T /C /Q >nul 2>&1
robocopy "%~dp0." "C:\ClubShellKit" *.ps1 *.cmd ClubShellSetup-1.0.18.exe /R:1 /W:1 /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 (echo Copy to C:\ClubShellKit failed. & pause & exit /b 1)
rem Control passes to the copy on C: (no "call"), so this file on the stick is not read again.
"C:\ClubShellKit\shell-install-run-as-admin.cmd"
:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0shell-install.ps1"
pause

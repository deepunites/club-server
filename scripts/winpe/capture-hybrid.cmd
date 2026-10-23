@echo off
rem Captures the generalized hybrid image from the reference PC (PC-11) to the server share incoming
rem (docs/imaging.md). Run it in WinPE booted from the USB stick made by build-winpe.cmd /usb, after seal-hybrid.ps1
rem (sysprep /generalize /oobe /shutdown) turned the PC off. The work is done by capture-hybrid.ps1 next to this file;
rem arguments are passed to it, for example: capture-hybrid.cmd -Drive C -Label h1-2026-10-24
rem English text only: the WinPE console has no Cyrillic font.
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" (echo ERROR: no PowerShell in this WinPE: make the stick again with build-winpe.cmd /usb & exit /b 1)
if not exist "%~dp0capture-hybrid.ps1" (echo ERROR: capture-hybrid.ps1 is missing next to %~nx0 & exit /b 1)
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0capture-hybrid.ps1" %*
set "RC=%errorlevel%"
if not "%RC%"=="0" echo capture-hybrid.ps1 stopped with exit code %RC%, see the messages above.
exit /b %RC%

@echo off
rem Club: read-only check of this hybrid PC before the ClubShell 1.0.18 install (docs/shell-install.md).
rem Right-click this file -> "Run as administrator". Works from the USB stick or from C:\ClubShellKit; changes nothing.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0shell-preflight.ps1"
pause

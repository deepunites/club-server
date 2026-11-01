@echo off
rem Club: removes ClubShell from this hybrid PC (rollback); the diskless helper is kept (docs/shell-install.md).
rem Right-click this file -> "Run as administrator". Run it from C:\ClubShellKit: USB sticks do not work while ClubShell is installed.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0shell-uninstall.ps1"
pause

@echo off
rem Club: sets the ClubShell kiosk admin PIN on this PC (Ctrl+Alt+Shift+A on the kiosk screen), typed by the owner.
rem Right-click this file -> "Run as administrator". ClubShell must be installed.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-shell-pin.ps1"
pause

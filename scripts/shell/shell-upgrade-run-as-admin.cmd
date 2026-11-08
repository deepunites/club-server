@echo off
rem Club: upgrades ClubShell on this PC to the version of the kit (1.0.20); the diskless helper goes to 1.9.0 first
rem if it is older. Usually the get-shell-kit command runs it by itself. Hold Shift at boot, sign in as Club, then
rem right-click this file -> "Run as administrator". Keeps agent.json, the registration, the PC key and the PIN.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0shell-upgrade.ps1"
pause

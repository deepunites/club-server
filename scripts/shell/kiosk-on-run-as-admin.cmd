@echo off
rem Club: turns the ClubShell kiosk on (auto-logon to "player" with the shell) once the PC is approved in the kassa.
rem Right-click this file -> "Run as administrator". Run it from C:\ClubShellKit: USB sticks do not work with ClubShell.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kiosk-on.ps1"
pause

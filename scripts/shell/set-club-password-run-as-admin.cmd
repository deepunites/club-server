@echo off
rem Club: sets the password of the local administrator "Club" (typed by the owner, hidden). Required before players.
rem Right-click this file -> "Run as administrator".
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-club-password.ps1"
pause

@echo off
rem Club: prepares the hybrid reference PC (PC-11) after it booted from its local SSD.
rem Right-click this file -> "Run as administrator".
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-hybrid.ps1"
pause

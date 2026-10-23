@echo off
rem Club: seals the hybrid reference PC and runs sysprep (the PC shuts down).
rem Right-click this file -> "Run as administrator".
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0seal-hybrid.ps1"
pause

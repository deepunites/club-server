@echo off
rem Club: ClubShell agent state on this PC (service, server, registration in the agent log). Read-only.
rem After the shell install USB sticks do not work: run it from C:\ClubShellKit.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0shell-agent-diag.ps1"
echo Take a photo of this window.
pause

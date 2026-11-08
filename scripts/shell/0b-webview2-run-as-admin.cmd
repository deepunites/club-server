@echo off
rem Club: Microsoft Edge WebView2 Runtime before the ClubShell install (the 1.0.20 bundle, like 1.0.18, carries an
rem outdated WebView2 hash and fails with 0x80091007 when it has to download WebView2 itself). Needs internet.
rem Then run shell-install-run-as-admin.cmd.
net session >nul 2>&1 || (echo Run this file as administrator: right-click it, "Run as administrator". & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-webview2.ps1"
pause

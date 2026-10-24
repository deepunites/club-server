@echo off
rem Club hybrid image: Windows Setup runs this as SYSTEM at the end of setup, before the first logon
rem (C:\Windows\Setup\Scripts\SetupComplete.cmd; skipped by Windows when an OEM product key is detected).
rem NVIDIA Control Panel: provisioned again with its Store license and Regions=all, see nvcp\Install-NVCP.ps1.
if exist "%WINDIR%\Setup\Scripts\nvcp\Install-NVCP.ps1" "%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%WINDIR%\Setup\Scripts\nvcp\Install-NVCP.ps1" -Mode System >> "%WINDIR%\Setup\Scripts\nvcp\setupcomplete.log" 2>&1
exit /b 0

@echo off
rem Club: installs Windows ADK (Deployment Tools) and the WinPE add-on on a diskless seat for build-winpe.cmd.
rem Run as administrator. The two Microsoft bootstrappers come from the club server (SHA256 checked); you accept
rem the license yourself. The seat is reset on reboot, so build WinPE in the same session, without rebooting.
setlocal
net session >nul 2>&1
if errorlevel 1 (echo Run this file as administrator: right click - Run as administrator. & pause & exit /b 1)
set "SRV=http://192.168.1.201:5080/pxe/v1/files/tools"
set "W=%SystemDrive%\ClubADK"
set "K=%ProgramFiles(x86)%\Windows Kits\10\Assessment and Deployment Kit"
if not exist "%W%" mkdir "%W%"

for /f %%F in ('powershell -NoProfile -Command "[int]((Get-PSDrive $env:SystemDrive.TrimEnd(':')).Free/1GB)"') do set "FREE=%%F"
if not defined FREE set "FREE=0"
echo Free space on %SystemDrive% : %FREE% GB
if %FREE% LSS 15 (echo WARN  less than 15 GB free: ADK + WinPE + build need about 15 GB. & pause)

call :get adksetup.exe ac6a930fdb5c2980ba5fefe606d47edaafcf5f647b4337411500d158ea77300f || goto :fail
call :get adkwinpesetup.exe d4de67acc83de253ccc941dd0590808d83726bdd8ea768a374a7beaff7949d6a || goto :fail

echo.
echo ==^> Step 1 of 2: Windows ADK.
echo     Accept the license. On the feature page tick ONLY "Deployment Tools", untick everything else.
start "" /wait "%W%\adksetup.exe"
if not exist "%K%\Deployment Tools\DandISetEnv.bat" (echo FAIL  Deployment Tools are not installed. & goto :fail)
echo PASS  Deployment Tools installed

echo.
echo ==^> Step 2 of 2: Windows PE add-on.
echo     Accept the license and install (it has one component). The download takes a while.
start "" /wait "%W%\adkwinpesetup.exe"
if not exist "%K%\Windows Preinstallation Environment\copype.cmd" (echo FAIL  WinPE add-on is not installed. & goto :fail)
if not exist "%K%\Windows Preinstallation Environment\amd64\WinPE_OCs\WinPE-PowerShell.cab" (echo FAIL  WinPE amd64 components are missing. & goto :fail)
echo PASS  WinPE add-on installed

echo.
echo Done. Do NOT reboot this PC. Next: build-winpe.cmd (ask for it).
pause
exit /b 0

:get
echo Downloading %1 ...
"%SystemRoot%\System32\curl.exe" -f -sS --retry 3 -o "%W%\%1" "%SRV%/%1"
if errorlevel 1 (echo FAIL  cannot download %1 from the club server. & exit /b 1)
certutil -hashfile "%W%\%1" SHA256 | findstr /i /x "%2" >nul
if errorlevel 1 (echo FAIL  SHA256 of %1 does not match. & exit /b 1)
echo PASS  %1 SHA256 OK
exit /b 0

:fail
echo.
echo Stopped. Send a photo of this window.
pause
exit /b 1

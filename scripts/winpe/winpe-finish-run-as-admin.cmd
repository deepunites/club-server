@echo off
rem Club: finishes the WinPE build after MakeWinPEMedia failed on the "active" step (GPT stick; 2026-10-23, PC-01).
rem The stick is already FAT32 with the label WINPE. UEFI boots from \EFI\Boot\bootx64.efi on FAT32, so the media files
rem are simply copied; no active partition or bootsect is needed (all club PCs boot UEFI, CSM off).
rem Also copies the network-boot files to this flash drive (winpe-out next to this file). Run as administrator.
setlocal
net session >nul 2>&1
if errorlevel 1 (echo Run this file as administrator: right click - Run as administrator. & pause & exit /b 1)
set "MEDIA=%SystemDrive%\ClubWinPE\work\media"
set "PXE=%SystemDrive%\ClubWinPE\out\pxe"
if not exist "%MEDIA%\sources\boot.wim" (echo FAIL  no %MEDIA%\sources\boot.wim: run winpe-run-as-admin.cmd again. & goto fail)
if not exist "%PXE%\sources\boot.wim" (echo FAIL  no %PXE%\sources\boot.wim: run winpe-run-as-admin.cmd again. & goto fail)
if not exist "%~dp0capture-hybrid.ps1" (echo FAIL  capture-hybrid.ps1 is missing next to this file. & goto fail)

set "USBL="
set "N=0"
for /f %%L in ('powershell -NoProfile -Command "Get-Volume | Where-Object { $_.DriveType -eq 'Removable' -and $_.FileSystemLabel -eq 'WinPE' -and $_.DriveLetter } | ForEach-Object { $_.DriveLetter }"') do (set "USBL=%%L" & set /a N+=1)
if not "%N%"=="1" (echo FAIL  expected exactly one USB stick with the label WINPE, found %N%. & goto fail)
if /i "%USBL%:"=="%~d0" (echo FAIL  %USBL%: is this flash drive with the scripts. & goto fail)
echo WinPE stick: %USBL%:

echo ==^> Writing WinPE to %USBL%:
robocopy "%MEDIA%" %USBL%:\ /e /r:2 /w:5 /np /njh /njs /nfl /ndl
if errorlevel 8 (echo FAIL  cannot copy WinPE to %USBL%:. & goto fail)
fc /b "%MEDIA%\sources\boot.wim" "%USBL%:\sources\boot.wim" >nul
if errorlevel 1 (echo FAIL  boot.wim on %USBL%: differs from the built one. & goto fail)
if not exist "%USBL%:\EFI\Boot\bootx64.efi" (echo FAIL  no \EFI\Boot\bootx64.efi on %USBL%:. & goto fail)
copy /y "%~dp0capture-hybrid.cmd" %USBL%:\ >nul || (echo FAIL  cannot copy capture-hybrid.cmd. & goto fail)
copy /y "%~dp0capture-hybrid.ps1" %USBL%:\ >nul || (echo FAIL  cannot copy capture-hybrid.ps1. & goto fail)
echo PASS  WinPE stick %USBL%: is ready

echo ==^> Copying the network-boot files to %~dp0winpe-out
robocopy "%PXE%" "%~dp0winpe-out" /e /r:2 /w:5 /np /njh /njs /nfl /ndl
if errorlevel 8 (echo FAIL  cannot copy to this flash drive. & goto fail)
certutil -hashfile "%~dp0winpe-out\sources\boot.wim" SHA256
echo.
echo Done. Bring this flash drive to the laptop. Keep the WINPE stick for PC-11.
pause
exit /b 0

:fail
echo.
echo Stopped. Send a photo of this window.
pause
exit /b 1

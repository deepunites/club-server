@echo off
rem Club: builds WinPE on this diskless seat (after adk-install.cmd, in the same session) with the Intel I219-V driver,
rem writes the capture USB stick and copies the network-boot files to this flash drive (winpe-out next to this file).
rem Run as administrator from the flash drive: build-winpe.cmd and capture-hybrid.* must be next to this file.
setlocal
net session >nul 2>&1
if errorlevel 1 (echo Run this file as administrator: right click - Run as administrator. & pause & exit /b 1)
set "SRV=http://192.168.1.201:5080/pxe/v1/files/tools"
set "DRVZIP=winpe-drv-e1d.zip"
set "DRVSHA=ffd2f77db63298b4f02bee617e3ec7307a7fc9e74b5f25632b857a4ba19de5dc"
set "D=%SystemDrive%\ClubWinPE-drv"
if not exist "%~dp0build-winpe.cmd" (echo FAIL  build-winpe.cmd is missing next to this file. & goto fail)

echo ==^> NIC driver for WinPE (Intel I219-V e1d 12.19.2.65) from the club server
if exist "%D%" rmdir /s /q "%D%"
mkdir "%D%" || goto fail
"%SystemRoot%\System32\curl.exe" -f -sS --retry 3 -o "%D%\%DRVZIP%" "%SRV%/%DRVZIP%"
if errorlevel 1 (echo FAIL  cannot download %DRVZIP% from the club server. & goto fail)
certutil -hashfile "%D%\%DRVZIP%" SHA256 | findstr /i /x "%DRVSHA%" >nul
if errorlevel 1 (echo FAIL  SHA256 of %DRVZIP% does not match. & goto fail)
"%SystemRoot%\System32\tar.exe" -xf "%D%\%DRVZIP%" -C "%D%"
if errorlevel 1 (echo FAIL  cannot unpack %DRVZIP%. & goto fail)
del "%D%\%DRVZIP%"
echo PASS  driver ready in %D%

echo.
echo ==^> USB stick for capturing the hybrid image
echo Plug in the EMPTY stick (8 GB or more) now. It will be FORMATTED, everything on it is lost.
echo Never choose %~d0 - that is this flash drive with the scripts.
powershell -NoProfile -Command "Get-Volume | Where-Object DriveType -eq 'Removable' | Sort-Object DriveLetter | Format-Table DriveLetter, FileSystemLabel, FileSystem, @{n='SizeGB'; e={[math]::Round($_.Size / 1GB, 1)}} -AutoSize"
set "USBL="
set /p "USBL=Drive letter of the EMPTY stick (only Enter = skip the stick): "
set "USBARG="
if not defined USBL goto nousb
set "USBL=%USBL::=%"
if /i "%USBL%:"=="%~d0" (echo FAIL  %USBL%: is this flash drive with the scripts. & goto fail)
set "USBARG=/usb %USBL%:"
:nousb

echo.
call "%~dp0build-winpe.cmd" /drivers "%D%" %USBARG%
if errorlevel 1 goto fail

echo.
echo ==^> Copying the network-boot files to %~dp0winpe-out
robocopy "%SystemDrive%\ClubWinPE\out\pxe" "%~dp0winpe-out" /e /r:2 /w:5 /np /njh /njs
if errorlevel 8 (echo FAIL  cannot copy to this flash drive. & goto fail)
certutil -hashfile "%~dp0winpe-out\sources\boot.wim" SHA256
echo.
echo Done. Bring this flash drive to the laptop. Do not reboot this PC until the files are checked.
pause
exit /b 0

:fail
echo.
echo Stopped. Send a photo of this window.
pause
exit /b 1

@echo off
rem Builds WinPE for network reinstall and for the capture USB stick (docs/imaging.md). Run as administrator on a
rem Windows PC with the ADK, best a diskless seat right after adk-install.cmd (same session: the seat resets on reboot):
rem   build-winpe.cmd [/drivers FOLDER] [/usb E:]
rem /drivers  every driver under the folder (subfolders too) goes into boot.wim (DISM /Add-Driver /Recurse): the NIC
rem           of every board that is reinstalled (Intel I219-V e1d NDIS68 now, Realtek RTL8111/8125 later). Without it
rem           WinPE has only inbox NIC drivers and the deploy script retries /deploy/v1/start forever.
rem /usb      also writes the same WinPE to that USB stick: after a typed YES it is FORMATTED FAT32 (label WINPE) and
rem           the media files are copied to it (UEFI boot only), then capture-hybrid.cmd and capture-hybrid.ps1 from
rem           this folder go to its root.
rem Output: %SystemDrive%\ClubWinPE\out\pxe with boot\BCD, boot\boot.sdi, sources\boot.wim and, if WinPE has it,
rem boot\bootx64.efi. Copy it to Imaging:PxeRoot on the server next to wimboot (paths as in BootFiles.cs).
rem The deploy script and server address are not baked in: wimboot injects them from the server on every boot. The USB
rem stick has none of them and starts a plain command prompt.
rem Work and output are on the local system drive: DISM cannot mount an image on a network share.
rem First real build: PC-01, 2026-10-23 (ADK 10.1.26100.9457, WinPE 10.0.26100.1). HYPOTHESIS: the USB step with
rem format and robocopy (MakeWinPEMedia failed there) has not run yet.
setlocal
rem The drive this script runs from (the flash drive with the scripts) is never a /usb target. Taken before the
rem argument loop.
set "SELFDRIVE=%~d0"
set "BASE=%SystemDrive%\ClubWinPE"
set "WORK=%BASE%\work"
set "OUT=%BASE%\out\pxe"
rem The confirmed stick is recorded outside WORK: the build deletes WORK.
set "USBIDFILE=%BASE%\usb-stick.txt"
set "ADK=%ProgramFiles(x86)%\Windows Kits\10\Assessment and Deployment Kit"
set "DRV="
set "USB="

:args
if "%~1"=="" goto argsdone
if /i "%~1"=="/drivers" goto argdrivers
if /i "%~1"=="/usb" goto argusb
if "%~1"=="/?" goto usage
echo ERROR: unknown argument %1
goto usage
:argdrivers
set "DRV=%~2"
if not defined DRV (echo ERROR: /drivers needs a folder & goto usage)
if "%DRV:~0,1%"=="/" (echo ERROR: /drivers needs a folder & goto usage)
set "DRV=%~f2"
rem No trailing backslash: robocopy reads a backslash before the closing quote as an escaped quote.
if "%DRV:~-1%"=="\" set "DRV=%DRV:~0,-1%"
shift /1
shift /1
goto args
:argusb
set "USB=%~2"
if not defined USB (echo ERROR: /usb needs a drive letter, for example /usb E: & goto usage)
if "%USB:~1%"=="" set "USB=%USB%:"
echo %USB%| findstr /r /i /x "[abcdefghijklmnopqrstuvwxyz]:" >nul || (echo ERROR: /usb needs a drive letter, for example /usb E: & goto usage)
shift /1
shift /1
goto args
:argsdone

net session >nul 2>&1
if errorlevel 1 (echo ERROR: run this as administrator & exit /b 1)
if not defined WinPERoot if exist "%ADK%\Deployment Tools\DandISetEnv.bat" call "%ADK%\Deployment Tools\DandISetEnv.bat" >nul
rem DandISetEnv.bat finds the kit through the registry (KitsRoot10); on PC-01 (2026-10-23) it left WinPERoot empty,
rem so the default install path is the fallback: copype needs WinPERoot and OSCDImgRoot.
if not defined WinPERoot if exist "%ADK%\Windows Preinstallation Environment\copype.cmd" call :adkenv
if not defined WinPERoot (echo ERROR: Windows ADK with the WinPE add-on not found: run adk-install.cmd first & exit /b 1)
echo ADK: %WinPERoot%
if not exist "%WinPERoot%\copype.cmd" (echo ERROR: no copype.cmd in "%WinPERoot%": install the WinPE add-on & exit /b 1)
set ARCH=amd64
set "OC=%WinPERoot%\%ARCH%\WinPE_OCs"

if not defined DRV goto drvchecked
if not exist "%DRV%\" (echo ERROR: drivers folder not found: "%DRV%" & exit /b 1)
dir /s /b "%DRV%\*.inf" >nul 2>&1
if errorlevel 1 (echo ERROR: no .inf files under "%DRV%" & exit /b 1)
:drvchecked

rem The stick is checked and confirmed before the build. Right before formatting it is checked again and must be the
rem same disk and partition as recorded here; a stick swapped during the build or a moved letter is refused.
if not defined USB goto usbchecked
if /i "%USB%"=="%SystemDrive%" (echo ERROR: %USB% is the system drive & exit /b 1)
if /i "%USB%"=="%SELFDRIVE%" (echo ERROR: %USB% is the drive %~nx0 runs from & exit /b 1)
if not exist "%~dp0capture-hybrid.cmd" (echo ERROR: capture-hybrid.cmd is missing next to %~nx0 & exit /b 1)
if not exist "%~dp0capture-hybrid.ps1" (echo ERROR: capture-hybrid.ps1 is missing next to %~nx0 & exit /b 1)
if not exist "%BASE%\" mkdir "%BASE%"
call :checkusb record || exit /b 1
echo.
echo WARNING: the USB stick %USB% shown above will be FORMATTED after the build. Everything on it will be lost.
set "ANSWER="
set /p "ANSWER=Type YES to use this stick, anything else cancels: "
if /i not "%ANSWER%"=="YES" (echo Cancelled, nothing was changed. & exit /b 1)
:usbchecked

rem Leftovers of an earlier run: an image still mounted in work\mount, old work and output folders.
if exist "%WORK%\mount\Windows" dism /Unmount-Image /MountDir:"%WORK%\mount" /Discard >nul 2>&1
if exist "%WORK%" rmdir /s /q "%WORK%"
if exist "%WORK%" (echo ERROR: cannot remove %WORK%: run "dism /Cleanup-Mountpoints" and try again & exit /b 1)
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" (echo ERROR: cannot remove %OUT% & exit /b 1)
call "%WinPERoot%\copype.cmd" %ARCH% "%WORK%" || (echo ERROR: copype failed & exit /b 1)
if not exist "%WORK%\media\sources\boot.wim" (echo ERROR: copype made no media\sources\boot.wim & exit /b 1)

rem Drivers are copied to the local disk first: the folder may be on the server share.
if not defined DRV goto drvcopied
echo Copying drivers from "%DRV%"...
robocopy "%DRV%" "%WORK%\drivers" /e /r:2 /w:5 /np /nfl /ndl /njh /njs
if errorlevel 8 (echo ERROR: cannot copy the drivers from "%DRV%": for a share run net use first & exit /b 1)
:drvcopied

dism /Mount-Image /ImageFile:"%WORK%\media\sources\boot.wim" /Index:1 /MountDir:"%WORK%\mount" || (echo ERROR: cannot mount boot.wim & exit /b 1)
rem Package order matters: PowerShell needs WMI, NetFX and Scripting; StorageWMI provides the disk cmdlets.
for %%p in (WinPE-WMI WinPE-NetFX WinPE-Scripting WinPE-PowerShell WinPE-StorageWMI WinPE-DismCmdlets) do (
  dism /Image:"%WORK%\mount" /Add-Package /PackagePath:"%OC%\%%p.cab" || goto fail
  if exist "%OC%\en-us\%%p_en-us.cab" dism /Image:"%WORK%\mount" /Add-Package /PackagePath:"%OC%\en-us\%%p_en-us.cab" || goto fail
)
rem NIC drivers must be inside boot.wim: without them WinPE has no network (lesson of PC-01, 2026-10-18).
if not defined DRV goto nodrivers
dism /Image:"%WORK%\mount" /Add-Driver /Driver:"%WORK%\drivers" /Recurse || goto fail
dism /Image:"%WORK%\mount" /Get-Drivers || goto fail
goto driversdone
:nodrivers
echo WARNING: no /drivers given: WinPE has only inbox NIC drivers (no Intel I219-V e1d, no Realtek RTL8125).
:driversdone
rem Scratch space on the WinPE RAM disk X: for DISM and PowerShell: 512 MB instead of the default 32 MB.
dism /Image:"%WORK%\mount" /Set-ScratchSpace:512 || goto fail
mkdir "%OUT%\boot" "%OUT%\sources" || goto fail
rem Boot manager signed with Windows UEFI CA 2023 for PCs that revoked PCA 2011 (dbx). The server checks its
rem signature (panel: Windows images -> network boot files). WinPE 10.0.26100.1 has it (PC-01, 2026-10-23).
if exist "%WORK%\mount\Windows\Boot\EFI_EX\bootmgfw_EX.efi" (
  copy /y "%WORK%\mount\Windows\Boot\EFI_EX\bootmgfw_EX.efi" "%OUT%\boot\bootx64.efi" || goto fail
) else (
  echo NOTE: no Windows\Boot\EFI_EX\bootmgfw_EX.efi in WinPE: PCs with PCA 2011 revoked will not boot it.
)
dism /Unmount-Image /MountDir:"%WORK%\mount" /Commit || (echo ERROR: cannot commit boot.wim & exit /b 1)
copy /y "%WORK%\media\Boot\BCD" "%OUT%\boot\BCD" || exit /b 1
copy /y "%WORK%\media\Boot\boot.sdi" "%OUT%\boot\boot.sdi" || exit /b 1
copy /y "%WORK%\media\sources\boot.wim" "%OUT%\sources\boot.wim" || exit /b 1
certutil -hashfile "%OUT%\sources\boot.wim" SHA256

if not defined USB goto done
rem UEFI boots \EFI\Boot\bootx64.efi from a FAT32 stick, so the media files are just copied: no active partition and
rem no bootsect, all club PCs boot UEFI only (CSM off). Not MakeWinPEMedia /UFD: on PC-01 (2026-10-23, Lexar 28.9 GB)
rem it formatted the stick and then failed on the diskpart "active" step (DiskPart errorlevel -2147024809, GPT or
rem removable disk). The network-boot output above is complete; winpe-finish-run-as-admin.cmd can redo this step.
if not exist "%WORK%\media\EFI\Boot\bootx64.efi" (echo ERROR: copype made no media\EFI\Boot\bootx64.efi & exit /b 1)
call :checkusb verify || exit /b 1
echo Formatting %USB% as FAT32 and copying WinPE to it...
rem The empty line answers "press ENTER when ready" that format may ask on removable media, /Y its Y/N questions;
rem /X dismounts the volume first if Explorer still has it open.
echo.| format %USB% /FS:FAT32 /Q /V:WINPE /X /Y
if errorlevel 1 (echo ERROR: cannot format %USB% & exit /b 1)
robocopy "%WORK%\media" %USB%\ /e /r:2 /w:5 /np /nfl /ndl /njh /njs
if errorlevel 8 (echo ERROR: cannot copy WinPE to %USB% & exit /b 1)
if not exist "%USB%\EFI\Boot\bootx64.efi" (echo ERROR: no \EFI\Boot\bootx64.efi on %USB% & exit /b 1)
fc /b "%WORK%\media\sources\boot.wim" "%USB%\sources\boot.wim" >nul || (echo ERROR: boot.wim on %USB% differs from the built one & exit /b 1)
copy /y "%~dp0capture-hybrid.cmd" "%USB%\" || (echo ERROR: cannot copy capture-hybrid.cmd to %USB% & exit /b 1)
copy /y "%~dp0capture-hybrid.ps1" "%USB%\" || (echo ERROR: cannot copy capture-hybrid.ps1 to %USB% & exit /b 1)
echo USB stick %USB% is ready. On the reference PC after sysprep: F12, UEFI: the stick, then run capture-hybrid.cmd
echo from the stick root (its letter in WinPE may differ).

:done
echo.
echo Done: %OUT%
echo Next: copy it to the server, for example robocopy "%OUT%" \\192.168.1.201\incoming\winpe-out /e, and from there
echo to Imaging:PxeRoot next to wimboot (boot\BCD, boot\boot.sdi, sources\boot.wim, boot\bootx64.efi if present).
exit /b 0

:fail
echo ERROR: the step above failed, boot.wim is discarded.
dism /Unmount-Image /MountDir:"%WORK%\mount" /Discard
exit /b 1

:checkusb
rem Only a partition on a USB disk that does not hold the running Windows; format /FS:FAT32 refuses over 32 GB.
powershell -NoProfile -Command "try { $p = Get-Partition -DriveLetter '%USB:~0,1%' -ErrorAction Stop; $d = Get-Disk -Number $p.DiskNumber -ErrorAction Stop; $v = Get-Volume -DriveLetter '%USB:~0,1%' -ErrorAction Stop } catch { Write-Host 'ERROR: no partition with the letter %USB% (a stick without a partition table? create one with diskpart)'; exit 1 }; Write-Host ('USB stick %USB% = disk {0}: {1}, {2:N1} GB, bus {3}; volume ''{4}'' {5}, {6:N1} GB' -f $d.Number, $d.FriendlyName, ($d.Size / 1GB), $d.BusType, $v.FileSystemLabel, $v.FileSystem, ($p.Size / 1GB)); if ([string]$d.BusType -ne 'USB') { Write-Host 'ERROR: %USB% is not on a USB disk'; exit 1 }; if ($d.IsBoot -or $d.IsSystem) { Write-Host 'ERROR: %USB% is on the disk of the running Windows'; exit 1 }; if ($p.Size -gt 32GB) { Write-Host 'ERROR: the partition is over 32 GB and cannot be formatted FAT32: make one of 32 GB or less with diskpart'; exit 1 }; exit 0"
if errorlevel 1 exit /b 1
rem Pins the stick: "record" (before the build) stores the disk UniqueId, serial number and size and the partition
rem offset and size in USBIDFILE; "verify" (right before formatting) refuses when the letter points to anything else.
powershell -NoProfile -Command "try { $p = Get-Partition -DriveLetter '%USB:~0,1%' -ErrorAction Stop; $d = Get-Disk -Number $p.DiskNumber -ErrorAction Stop } catch { Write-Host 'ERROR: no partition with the letter %USB%'; exit 1 }; $u = ([string]$d.UniqueId).Trim(); $s = ([string]$d.SerialNumber).Trim(); if (-not ($u + $s)) { Write-Host 'ERROR: the disk of %USB% has no UniqueId and no serial number, so it cannot be pinned: use another stick'; exit 1 }; $id = 'UniqueId={0} SerialNumber={1} DiskSize={2} PartitionOffset={3} PartitionSize={4}' -f $u, $s, $d.Size, $p.Offset, $p.Size; if ('%~1' -eq 'record') { try { Set-Content -LiteralPath $env:USBIDFILE -Value $id -Encoding UTF8 -ErrorAction Stop } catch { Write-Host ('ERROR: cannot write ' + $env:USBIDFILE); exit 1 }; exit 0 }; $old = ([string](Get-Content -LiteralPath $env:USBIDFILE -Raw -ErrorAction SilentlyContinue)).Trim(); if ($old -cne $id) { Write-Host 'ERROR: %USB% is not the stick confirmed before the build (swapped, or the letter moved): nothing was formatted'; Write-Host ('  confirmed: ' + $old); Write-Host ('  now:       ' + $id); exit 1 }; Write-Host 'Same stick and partition as confirmed before the build.'; exit 0"
exit /b %errorlevel%

:adkenv
set "WinPERoot=%ADK%\Windows Preinstallation Environment"
set "OSCDImgRoot=%ADK%\Deployment Tools\amd64\Oscdimg"
set "DISMRoot=%ADK%\Deployment Tools\amd64\DISM"
set "BCDBootRoot=%ADK%\Deployment Tools\amd64\BCDBoot"
set "PATH=%DISMRoot%;%BCDBootRoot%;%OSCDImgRoot%;%WinPERoot%;%PATH%"
exit /b 0

:usage
echo Usage: %~nx0 [/drivers ^<folder^>] [/usb ^<letter^>:]
echo   /drivers  folder with NIC drivers (.inf with their files, subfolders included) to add to boot.wim
echo   /usb      also write WinPE to this USB stick: it is FORMATTED, everything on it is lost
exit /b 1

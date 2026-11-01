@echo off
rem Club server: board introduction for the diskless golden image (docs/diskless-pilot.md, sections 7 and 10).
rem A board whose network card the golden has never seen stops with 0x7B. To introduce it, the PC gets a temporary
rem local SSD, the reference WIM of the golden (make-hybrid-ref.py, built from the snapshot the golden still equals:
rem check `written@<that snapshot>` = 0 on TrueNAS before this step) is deployed to it by a reimage job and booted
rem ONCE from the SSD with the network cable out: Windows installs the board's devices (network card, chipset, video).
rem This script then copies what changed on the SSD back into the golden. It replaces install.cmd in the winsetup
rem share while the master is in "install" mode on that PC (WinPE with the golden attached over iSCSI); put
rem install.setup.cmd back afterwards.
rem   1. finds exactly one Windows volume on an iSCSI disk (the golden) and exactly one on a local disk (the SSD);
rem   2. checks that the SSD has more PCI network cards than the golden and that both use ControlSet001;
rem   3. saves from the golden: MountedDevices and the storage/iSCSI services the reference WIM changes (storahci,
rem      stornvme, MSiSCSI, iScsiPrt) - their StartOverride subkeys go, the export brings back the golden's values;
rem   4. robocopy of the files changed on the SSD into the golden (nothing is deleted in the golden), without the
rem      helper state, the reimage unattend.xml, pagefile and System Volume Information; full file list in the log;
rem   5. puts the saved keys back; every service of a PCI network card (class Net) gets Start=0 and BootFlags=1 like
rem      install.cmd does; iScsiPrt and those services are checked: Start=0, BootFlags=1, no StartOverride;
rem   6. writes the log to the golden (Windows\Temp\club-introduce-board.log) and shuts the PC down.
rem A STOP after step 4 leaves the golden NOT bootable: roll it back to the snapshot (master off).
setlocal enableextensions
set LOG=X:\club-introduce-board.log
set NET={4d36e972-e325-11ce-bfc1-08002be10318}
echo Club server: copy the board introduction from the local SSD into the diskless golden image
reg add HKLM\SYSTEM\CurrentControlSet\Control\CrashControl /v AutoReboot /t REG_DWORD /d 0 /f >nul
rem Drivers already in boot.wim (DISM) are installed at boot; loading them again restarts the NIC and drops the
rem iSCSI boot session (WinPE then loses the system image disk and reboots).
for %%i in (%SystemRoot%\System32\*.inf) do (pnputil /enum-drivers | find /i "%%~nxi" >nul && echo %%~nxi is already installed || drvload "%%i")
wpeinit
wpeutil InitializeNetwork
start "Club diagnostics" cmd /k ipconfig /all
for %%t in (robocopy.exe diskpart.exe reg.exe) do if not exist %SystemRoot%\System32\%%t (echo STOP: %%t is not in this WinPE & goto stop)

set /a TRIES=0
:find
set GOLD=
set LOCAL=
set /a NG=0
set /a NL=0
for %%d in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do if exist %%d:\Windows\System32\config\SYSTEM call :classify %%d
if %NG% equ 1 if %NL% equ 1 goto found
set /a TRIES+=1
if %TRIES% lss 36 (echo Waiting for the golden on the iSCSI disk and the local SSD, try %TRIES% of 36: iSCSI %NG%, local %NL% & ping -n 6 127.0.0.1 >nul & goto find)
echo.
echo STOP: need exactly one Windows on an iSCSI disk and one on a local disk, found iSCSI %NG%, local %NL%.
iscsicli SessionList
goto stop

:found
echo Golden (iSCSI disk): %GOLD%:   local SSD: %LOCAL%:
echo Golden %GOLD%:  local SSD %LOCAL%: > %LOG%
reg load HKLM\CLUBG %GOLD%:\Windows\System32\config\SYSTEM >nul || (echo STOP: cannot load the golden SYSTEM hive & goto stop)
reg load HKLM\CLUBL %LOCAL%:\Windows\System32\config\SYSTEM >nul || (echo STOP: cannot load the SSD SYSTEM hive & reg unload HKLM\CLUBG >nul & goto stop)
set GCS=
set LCS=
for /f "tokens=3" %%a in ('reg query HKLM\CLUBG\Select /v Current ^| find /i "Current"') do set /a GCS=%%a
for /f "tokens=3" %%a in ('reg query HKLM\CLUBL\Select /v Current ^| find /i "Current"') do set /a LCS=%%a
echo Control set: golden %GCS%, SSD %LCS%
if not "%GCS%"=="1" goto csmismatch
if not "%LCS%"=="1" goto csmismatch

rem Network cards (PCI devices of class Net) in each hive: the SSD must have more than the golden.
call :countnics CLUBG
set /a GN=%COUNT%
call :countnics CLUBL
set /a LN=%COUNT%
echo Network cards: golden %GN%, SSD %LN%
echo Network cards on the SSD (instance, Service, ConfigFlags, Driver): >> %LOG%
call :listnics CLUBL
type %LOG%
if %LN% leq %GN% (echo STOP: the SSD has no network card the golden does not have - was Windows booted from the SSD? & goto unloadstop)

rem Saved from the golden before the copy: drive letters and the boot-relevant services.
rem Storage and iSCSI services as the golden has them. NIC services are NOT restored from the golden: the SSD may have
rem installed the new card from another package under the same service name (ImagePath in DriverStore), so they keep
rem the SSD's values and only get Start=0/BootFlags=1 below. Not "every service with BootFlags" either: a whole Tcpip
rem export would overwrite its Linkage lists and unbind the new card.
set SAVED=storahci stornvme MSiSCSI iScsiPrt
echo Services saved from the golden: %SAVED%
echo Services saved from the golden: %SAVED% >> %LOG%
del /q X:\club-save-*.reg 2>nul
reg export HKLM\CLUBG\MountedDevices X:\club-save-MountedDevices.reg /y >nul || (echo STOP: export of MountedDevices failed & goto unloadstop)
for %%s in (%SAVED%) do reg query HKLM\CLUBG\ControlSet001\Services\%%s >nul 2>&1 && (reg export HKLM\CLUBG\ControlSet001\Services\%%s X:\club-save-%%s.reg /y >nul || (echo STOP: export of %%s failed & goto unloadstop))
reg unload HKLM\CLUBL >nul || (echo STOP: cannot unload the SSD hive & reg unload HKLM\CLUBG >nul 2>&1 & goto stop)
reg unload HKLM\CLUBG >nul || (echo STOP: cannot unload the golden hive & goto stop)

echo.
echo Copying the files changed on the SSD into the golden (a few minutes, file list in X:\club-robocopy.log)
robocopy %LOCAL%:\ %GOLD%:\ /E /B /COPYALL /DCOPY:DAT /XJ /R:1 /W:1 /NP /NDL /FP /LOG:X:\club-robocopy.log /XF pagefile.sys swapfile.sys hiberfil.sys DumpStack.log DumpStack.log.tmp /XD "%LOCAL%:\System Volume Information" "%LOCAL%:\$Recycle.Bin" "%LOCAL%:\Windows\Panther" "%LOCAL%:\ProgramData\ClubDiskless" "%LOCAL%:\Recovery" "%LOCAL%:\$WinREAgent" "%LOCAL%:\AMD" "%LOCAL%:\Intel" "%LOCAL%:\ClubDrivers" "%LOCAL%:\Windows\SoftwareDistribution\Download"
set RC=%errorlevel%
echo robocopy exit code %RC% (0-7 = success)
echo robocopy exit code %RC% >> %LOG%
call :tail 14 X:\club-robocopy.log
if %RC% geq 8 (echo STOP: robocopy failed & goto stopcopied)
set /a MISMATCH="RC & 4"
if not %MISMATCH%==0 echo WARNING: robocopy reports mismatched files/folders - see the log on the golden

echo Putting back the golden's drive letters and boot services
reg load HKLM\CLUBG %GOLD%:\Windows\System32\config\SYSTEM >nul || (echo STOP: cannot load the copied SYSTEM hive & goto stopcopied)
reg delete HKLM\CLUBG\MountedDevices /va /f >nul
reg import X:\club-save-MountedDevices.reg >nul 2>&1 || (echo STOP: import of MountedDevices failed & goto unloadcopied)
for %%s in (%SAVED%) do if exist X:\club-save-%%s.reg (reg delete HKLM\CLUBG\ControlSet001\Services\%%s\StartOverride /f >nul 2>&1 & reg import X:\club-save-%%s.reg >nul 2>&1 || (echo STOP: import of %%s failed & goto unloadcopied))
rem Every PCI network card service boot-start, as install.cmd does for the boot NIC (a new board may bring a new one).
set NICSVC=
for /f "delims=" %%k in ('reg query HKLM\CLUBG\ControlSet001\Enum\PCI /s /f %NET% /d /e 2^>nul ^| find /i "\Enum\PCI\"') do call :nicsvc "%%k"
echo Network card services: %NICSVC%
echo Network card services: %NICSVC% >> %LOG%
if "%NICSVC%"=="" (echo STOP: no network card service found in the copied hive & goto unloadcopied)
rem Only services that exist (ImagePath): reg add would create a key without a driver for a phantom devnode (0x7B).
for %%s in (rt640x64 rtcx21x64 e1dexpress e1iexpress e2fexpress %NICSVC%) do reg query HKLM\CLUBG\ControlSet001\Services\%%s /v ImagePath >nul 2>&1 && (reg add HKLM\CLUBG\ControlSet001\Services\%%s /v Start /t REG_DWORD /d 0 /f >nul & reg add HKLM\CLUBG\ControlSet001\Services\%%s /v BootFlags /t REG_DWORD /d 1 /f >nul & reg delete HKLM\CLUBG\ControlSet001\Services\%%s\StartOverride /f >nul 2>&1)
echo Golden after the copy (service Start BootFlags): >> %LOG%
set BAD=
for %%s in (iScsiPrt %NICSVC%) do call :checkboot %%s
echo Network cards in the golden now (instance, Service, ConfigFlags, Driver): >> %LOG%
call :listnics CLUBG
reg unload HKLM\CLUBG >nul || (echo STOP: cannot unload the golden hive & goto stopcopied)
copy /y %LOG% %GOLD%:\Windows\Temp\club-introduce-board.log >nul
copy /y X:\club-robocopy.log %GOLD%:\Windows\Temp\club-introduce-board-robocopy.log >nul
echo.
find /v "" %LOG% | findstr /i /c:"Golden" /c:"Network" /c:"VEN_" /c:"Service" /c:" Start " /c:"saved" /c:"exit code"
if defined BAD (echo STOP: not boot-ready over iSCSI:%BAD% & goto stopcopied)
echo.
echo DONE. Take a photo of this screen and tell the operator. Then press any key: the PC shuts down,
echo take the SSD out and switch the PC on again only when the operator says so.
pause >nul
wpeutil shutdown
goto :eof

:csmismatch
echo STOP: the control set is not ControlSet001 in both hives (golden %GCS%, SSD %LCS%)
:unloadstop
reg unload HKLM\CLUBL >nul 2>&1
reg unload HKLM\CLUBG >nul 2>&1
:stop
echo.
echo The golden was NOT changed. Take a photo of this screen and tell the operator.
cmd
goto :eof

:unloadcopied
reg unload HKLM\CLUBG >nul 2>&1
:stopcopied
copy /y %LOG% %GOLD%:\Windows\Temp\club-introduce-board.log >nul 2>&1
copy /y X:\club-robocopy.log %GOLD%:\Windows\Temp\club-introduce-board-robocopy.log >nul 2>&1
echo.
echo The golden IS ALREADY CHANGED and must not boot over iSCSI as it is: the operator rolls it back to the
echo snapshot (master off). Take a photo of this screen and tell the operator.
cmd
goto :eof

:classify
rem The golden is the volume on an iSCSI disk (diskpart "detail disk" shows the bus type), the SSD - any other.
> X:\club-dp.txt echo select volume %1
>> X:\club-dp.txt echo detail disk
diskpart /s X:\club-dp.txt > X:\club-dp.out 2>&1
find /i "iSCSI" X:\club-dp.out >nul
if errorlevel 1 (set LOCAL=%1& set /a NL+=1) else (set GOLD=%1& set /a NG+=1)
goto :eof

:countnics
rem Wired cards only (PCI class 0200; Wi-Fi 0280 is class Net too) that have a driver service.
set /a COUNT=0
for /f "delims=" %%k in ('reg query HKLM\%1\ControlSet001\Enum\PCI /s /f %NET% /d /e 2^>nul ^| find /i "\Enum\PCI\"') do reg query "%%k" /v Service >nul 2>&1 && (reg query "%%k" /v CompatibleIDs 2>nul | find /i "CC_0200" >nul && set /a COUNT+=1)
goto :eof

:listnics
for /f "delims=" %%k in ('reg query HKLM\%1\ControlSet001\Enum\PCI /s /f %NET% /d /e 2^>nul ^| find /i "\Enum\PCI\"') do (
  echo %%k>> %LOG%
  reg query "%%k" /v Service 2>nul | find /i "Service" >> %LOG%
  reg query "%%k" /v ConfigFlags 2>nul | find /i "ConfigFlags" >> %LOG%
  reg query "%%k" /v Driver 2>nul | find /i "Driver" >> %LOG%
)
goto :eof

:nicsvc
for /f "tokens=3" %%v in ('reg query %1 /v Service 2^>nul ^| find /i "Service"') do (
  for %%x in (%NICSVC%) do if /i "%%x"=="%%v" goto :eof
  reg query HKLM\CLUBG\ControlSet001\Services\%%v /v ImagePath >nul 2>&1 || (echo   %%v: network device service has no key - not touched>> %LOG%& goto :eof)
  call set NICSVC=%%NICSVC%% %%v
)
goto :eof

:checkboot
set S=
set B=
for /f "tokens=3" %%v in ('reg query HKLM\CLUBG\ControlSet001\Services\%1 /v Start 2^>nul ^| find /i "Start"') do set S=%%v
for /f "tokens=3" %%v in ('reg query HKLM\CLUBG\ControlSet001\Services\%1 /v BootFlags 2^>nul ^| find /i "BootFlags"') do set B=%%v
echo   %1 Start %S% BootFlags %B%>> %LOG%
if not "%S%"=="0x0" set BAD=%BAD% %1:Start=%S%
if not "%B%"=="0x1" set BAD=%BAD% %1:BootFlags=%B%
reg query HKLM\CLUBG\ControlSet001\Services\%1\StartOverride >nul 2>&1 && set BAD=%BAD% %1:StartOverride
reg query HKLM\CLUBG\ControlSet001\Services\%1 /v ImagePath >nul 2>&1 || set BAD=%BAD% %1:noImagePath
goto :eof

:tail
set /a N=0
for /f %%n in ('find /c /v "" ^< %2') do set /a N=%%n
set /a SKIP=N-%1
if %SKIP% lss 0 set /a SKIP=0
more +%SKIP% %2
goto :eof

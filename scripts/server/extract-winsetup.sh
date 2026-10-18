#!/bin/sh
# Prepares Windows Setup for the diskless master mode with "install" (docs/diskless-full.md §5), following
# ipxe.org/howto/winpe: iPXE hooks the system image (sanhook) and wimboots Setup's boot.wim over HTTP with two injected
# files — winpeshl.ini runs install.cmd, which starts the network, maps the read-only SMB share with the whole ISO
# (boot.wim alone has no install.wim) and runs setup.exe from it. Windows 11 hardware checks are bypassed (LabConfig):
# the club has Ryzen 5 1600 PCs.
# Run on the club server as root:  sh extract-winsetup.sh <Win11.iso> [destination]
# Keep the ISO where it is: it stays mounted read-only at /srv/club/winsetup (fstab) as the share "winsetup".
# Result (default /srv/club/pxe/winsetup, served as /pxe/v1/files/winsetup/…): boot/bcd, boot/boot.sdi,
# sources/boot.wim, install.cmd, winpeshl.ini. Re-running is safe (new ISO → remounted, files replaced).
set -eu
ISO=${1:?usage: extract-winsetup.sh <Windows ISO> [destination]}
DEST=${2:-/srv/club/pxe/winsetup}
SHARE=/srv/club/winsetup
ENV=/etc/club-server/club-server.env
PASSFILE=/etc/club-server/winsetup.password
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }
[ -f "$ISO" ] || { echo "no $ISO" >&2; exit 1; }
ISO=$(realpath "$ISO")
SERVER=$(sed -n 's|^Imaging__PublicBaseUrl=https\{0,1\}://\([^:/]*\).*|\1|p' "$ENV" | head -n1)
[ -n "$SERVER" ] || { echo "Imaging__PublicBaseUrl not set in $ENV" >&2; exit 1; }

# The ISO's files live in UDF: mounted read-only (loop), nothing on the ISO is changed; fstab keeps the share after reboots.
# A re-run with the same ISO keeps the mount (Samba may hold it open after a WinPE session: umount would be "busy") and
# only rewrites the WinPE files below; another ISO is remounted. The mount point is unmounted before install -d: on the
# read-only ISO its chmod would fail.
MOUNTED=$(losetup -n -O BACK-FILE "$(findmnt -n -o SOURCE "$SHARE" 2>/dev/null || echo none)" 2>/dev/null | sed 's/[[:space:]]*$//' || true)
if [ "$MOUNTED" != "$ISO" ]; then
    if mountpoint -q "$SHARE"; then umount "$SHARE"; fi
    install -d -m 755 "$SHARE"
    sed -i "\| $SHARE |d" /etc/fstab
    echo "$ISO $SHARE udf ro,loop,nofail 0 0" >> /etc/fstab
    mount "$SHARE"
fi

# Names on the ISO differ in case between releases (boot/bcd vs boot/BCD): look them up case-insensitively.
find_one() { # dir name
    found=$(find "$SHARE" -maxdepth 2 -ipath "$SHARE/$1/$2" -type f | head -n1)
    [ -n "$found" ] || { echo "$1/$2 not found on $ISO — is it a Windows install ISO?" >&2; exit 1; }
    printf '%s' "$found"
}
BCD=$(find_one boot bcd)
SDI=$(find_one boot boot.sdi)
WIM=$(find_one sources boot.wim)
SETUP=$(find_one sources setup.exe)
find "$SHARE/sources" -maxdepth 1 \( -iname install.wim -o -iname install.esd -o -iname install.swm \) | grep -q . \
    || { echo "sources/install.wim (or .esd) not found on $ISO" >&2; exit 1; }

install -d -m 755 "$DEST/boot" "$DEST/sources"
cp "$BCD" "$DEST/boot/bcd.partial" && mv "$DEST/boot/bcd.partial" "$DEST/boot/bcd"
cp "$SDI" "$DEST/boot/boot.sdi.partial" && mv "$DEST/boot/boot.sdi.partial" "$DEST/boot/boot.sdi"
# boot.wim with the club's NIC drivers added by DISM (scripts/golden/add-winpe-driver.ps1, docs/diskless-pilot.md):
# WinPE connects the iSCSI system image from the iBFT only if the NIC driver is in boot.wim at boot — drvload later is
# too late. It is kept as sources/boot.drivers.wim and served as boot.wim; a new ISO makes it stale, so it is dropped.
cp "$WIM" "$DEST/sources/boot.orig.wim.partial" && mv "$DEST/sources/boot.orig.wim.partial" "$DEST/sources/boot.orig.wim"
if [ "$MOUNTED" != "$ISO" ] && [ -f "$DEST/sources/boot.drivers.wim" ]; then
    echo "WARNING: new ISO — removing $DEST/sources/boot.drivers.wim (built from the old boot.wim); add the NIC drivers again" >&2
    rm -f "$DEST/sources/boot.drivers.wim"
fi
if [ -f "$DEST/sources/boot.drivers.wim" ]; then
    cp "$DEST/sources/boot.drivers.wim" "$DEST/sources/boot.wim.partial"
else
    echo "NOTE: boot.wim has no added NIC drivers yet — add them (scripts/golden/add-winpe-driver.ps1) before installing" >&2
    cp "$WIM" "$DEST/sources/boot.wim.partial"
fi
mv "$DEST/sources/boot.wim.partial" "$DEST/sources/boot.wim"

# Read-only SMB user for the share. Its password is not a secret worth guarding (it opens only a public Windows ISO),
# but WinPE of Windows 11 24H2 refuses guest access, so a user is needed; it is written into install.cmd.
id winsetup >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin winsetup
if [ ! -s "$PASSFILE" ]; then
    (umask 077; openssl rand -hex 12 > "$PASSFILE")
fi
PASS=$(cat "$PASSFILE")
printf '%s\n%s\n' "$PASS" "$PASS" | smbpasswd -s -a winsetup >/dev/null
if ! grep -q '^\[winsetup\]' /etc/samba/smb.conf; then
    cat >> /etc/samba/smb.conf <<CONF

[winsetup]
   path = $SHARE
   valid users = winsetup
   read only = yes
   browseable = no
CONF
fi
# The ISO's files have no Unix x bit: without this Samba refuses to run setup.exe ("Access denied", Setup exit code 5).
if ! sed -n '/^\[winsetup\]/,/^\[/p' /etc/samba/smb.conf | grep -q 'acl allow execute always'; then
    sed -i '/^\[winsetup\]$/a\   acl allow execute always = yes' /etc/samba/smb.conf
fi
testparm -s >/dev/null 2>&1
systemctl reload smbd 2>/dev/null || systemctl restart smbd

# Batch and ini files for WinPE: CRLF line endings. Setup is started from sources\ (the "previous version" of Setup:
# it honours LabConfig, finds install.wim next to itself and, unlike the 24H2 UI, lists iSCSI disks).
# NIC drivers (docs/diskless-pilot.md, verified on the club's MSI B560M / I219-V on 2026-10-18):
# - in boot.wim (DISM, see above) they are installed at boot and WinPE logs into the iBFT target itself; a driver that
#   is already installed must NOT be drvload-ed again: that restarts the NIC, drops the iSCSI boot session, and WinPE
#   loses the disk and reboots a few minutes later;
# - the files in $DEST/drv/ are added by the server as wimboot initrd (X:\Windows\System32): drvload for a NIC that has
#   no driver yet (WinPE gets a network, but no iSCSI disk), and the source for DISM into the installed Windows.
# The iBFT must carry no gateway (Diskless__ClearIbftGateway=true): otherwise the Windows initiator routes the target
# through the router, which hairpins/NATs it (TrueNAS shows the router's WAN address) and the session breaks.
# Setup starts only after the iSCSI boot session is logged in (TSID not 00 00), else it would see no disk. After
# Setup's first phase (/noreboot) the installed Windows is prepared for its first iSCSI boot (research:
# NiKiZe/wimboot-install, MS iSCSI boot): the drivers are added with DISM, NIC services and iScsiPrt get boot-start +
# BootFlags=1 (only services that exist), crash dumps, device encryption and Fast Startup are off. A WinPE crash stays
# on screen (AutoReboot 0), and a "Club diagnostics" console is open for ipconfig / iscsicli / diskpart.
install -d -m 755 "$DEST/drv"
SETUP_REL=$(printf '%s' "${SETUP#"$SHARE"/}" | tr '/' '\\')
printf '%s\r\n' \
    '@echo off' \
    'echo Club server: Windows setup onto the diskless system image' \
    'reg add HKLM\SYSTEM\CurrentControlSet\Control\CrashControl /v AutoReboot /t REG_DWORD /d 0 /f >nul' \
    'for %%i in (%SystemRoot%\System32\*.inf) do (pnputil /enum-drivers | find /i "%%~nxi" >nul && echo %%~nxi is already installed || drvload "%%i")' \
    'wpeinit' \
    'wpeutil InitializeNetwork' \
    'wpeutil WaitForNetwork' \
    'start "Club diagnostics" cmd /k ipconfig /all' \
    'set /a TRIES=0' \
    ':iscsi' \
    'iscsicli SessionList | findstr /r /c:"TSID: *00 00" >nul || goto iscsiok' \
    'set /a TRIES+=1' \
    'if %TRIES% lss 36 (echo Waiting for the iSCSI boot session to log in, try %TRIES% of 36 & ping -n 6 127.0.0.1 >nul & goto iscsi)' \
    'echo The iSCSI boot session (iBFT) did not log in: Setup would see no disk. Check the diagnostics window.' \
    ':iscsiok' \
    'iscsicli SessionList' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f >nul' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f >nul' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f >nul' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f >nul' \
    ':share' \
    "net use W: \\\\$SERVER\\winsetup /user:winsetup $PASS" \
    'if errorlevel 1 (' \
    "  echo The share \\\\$SERVER\\winsetup is not reachable yet, retrying in 5 s" \
    '  ipconfig | find "IPv4"' \
    '  ping -n 6 127.0.0.1 >nul' \
    '  goto share' \
    ')' \
    "W:\\$SETUP_REL /noreboot" \
    'set SETUPRC=%errorlevel%' \
    'echo Windows Setup finished with exit code %SETUPRC%' \
    'echo Preparing the installed Windows for booting over iSCSI' \
    'set T=' \
    'for %%d in (C D E F G H I J K L M N O P Q R S T U V Y Z) do if exist %%d:\Windows\System32\config\SYSTEM set T=%%d' \
    'if not defined T goto nowindows' \
    'for %%i in (%SystemRoot%\System32\*.inf) do dism /Image:%T%:\ /Add-Driver /Driver:"%%i"' \
    'reg load HKLM\CLUBT %T%:\Windows\System32\config\SYSTEM' \
    'for %%s in (rt640x64 rtcx21x64 e1dexpress e1iexpress e2fexpress) do reg query HKLM\CLUBT\ControlSet001\Services\%%s >nul 2>&1 && reg add HKLM\CLUBT\ControlSet001\Services\%%s /v Start /t REG_DWORD /d 0 /f && reg add HKLM\CLUBT\ControlSet001\Services\%%s /v BootFlags /t REG_DWORD /d 1 /f' \
    'reg add HKLM\CLUBT\ControlSet001\Services\iScsiPrt /v BootFlags /t REG_DWORD /d 1 /f' \
    'reg add HKLM\CLUBT\ControlSet001\Control\CrashControl /v CrashDumpEnabled /t REG_DWORD /d 0 /f' \
    'reg add HKLM\CLUBT\ControlSet001\Control\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f' \
    'reg add "HKLM\CLUBT\ControlSet001\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 0 /f' \
    'reg unload HKLM\CLUBT' \
    'goto reboot' \
    ':nowindows' \
    'echo.' \
    'echo Windows was NOT installed (Setup exit code %SETUPRC%). Setup error log:' \
    'for %%f in ("X:\$WINDOWS.~BT\Sources\Panther\setuperr.log" "X:\Windows\Panther\setuperr.log") do if exist %%f type %%f' \
    'echo.' \
    'echo Take a photo of this screen. Press any key to restart the PC.' \
    'pause >nul' \
    ':reboot' \
    'wpeutil reboot' \
    > "$DEST/install.cmd.partial"
mv "$DEST/install.cmd.partial" "$DEST/install.cmd"
printf '%s\r\n' '[LaunchApps]' '"install.cmd"' > "$DEST/winpeshl.ini"
chmod 644 "$DEST/boot/bcd" "$DEST/boot/boot.sdi" "$DEST/sources/boot.wim" "$DEST/install.cmd" "$DEST/winpeshl.ini"

ls -la "$DEST" "$DEST/boot" "$DEST/sources"
printf 'OK: Windows Setup in %s, installation files shared as \\\\%s\\winsetup (diskless master mode with install)\n' "$DEST" "$SERVER"

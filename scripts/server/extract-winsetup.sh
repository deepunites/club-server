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
install -d -m 755 "$SHARE"
if mountpoint -q "$SHARE"; then umount "$SHARE"; fi
sed -i "\| $SHARE |d" /etc/fstab
echo "$ISO $SHARE udf ro,loop,nofail 0 0" >> /etc/fstab
mount "$SHARE"

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
cp "$WIM" "$DEST/sources/boot.wim.partial" && mv "$DEST/sources/boot.wim.partial" "$DEST/sources/boot.wim"

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
testparm -s >/dev/null 2>&1
systemctl reload smbd 2>/dev/null || systemctl restart smbd

# Batch and ini files for WinPE: CRLF line endings. Setup is started from sources\ (the "previous version" of Setup:
# it honours LabConfig and finds install.wim next to itself).
SETUP_REL=$(printf '%s' "${SETUP#"$SHARE"/}" | tr '/' '\\')
printf '%s\r\n' \
    '@echo off' \
    'echo Club server: Windows setup onto the diskless system image' \
    'wpeinit' \
    'wpeutil WaitForNetwork' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f >nul' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f >nul' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f >nul' \
    'reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f >nul' \
    ':share' \
    "net use W: \\\\$SERVER\\winsetup /user:winsetup $PASS" \
    'if errorlevel 1 (' \
    "  echo The share \\\\$SERVER\\winsetup is not reachable yet, retrying in 5 s" \
    '  ping -n 6 127.0.0.1 >nul' \
    '  goto share' \
    ')' \
    "W:\\$SETUP_REL" \
    > "$DEST/install.cmd.partial"
mv "$DEST/install.cmd.partial" "$DEST/install.cmd"
printf '%s\r\n' '[LaunchApps]' '"install.cmd"' > "$DEST/winpeshl.ini"
chmod 644 "$DEST/boot/bcd" "$DEST/boot/boot.sdi" "$DEST/sources/boot.wim" "$DEST/install.cmd" "$DEST/winpeshl.ini"

ls -la "$DEST" "$DEST/boot" "$DEST/sources"
printf 'OK: Windows Setup in %s, installation files shared as \\\\%s\\winsetup (diskless master mode with install)\n' "$DEST" "$SERVER"

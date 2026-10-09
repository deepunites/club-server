#!/bin/sh
# Copies the Windows Setup boot files from a Windows 10/11 ISO for the diskless master mode with "install"
# (docs/diskless-full.md §5): wimboot boots Windows Setup over HTTP while iPXE has the system image hooked (sanhook).
# Run on the club server as root:  sh extract-winsetup.sh <Win11.iso> [destination]
# Result (default /srv/club/pxe/winsetup): boot/bcd, boot/boot.sdi, sources/boot.wim — served as /pxe/v1/files/winsetup/…
# The ISO's files live in UDF: it is mounted read-only (loop), nothing on the ISO is changed.
set -eu
ISO=${1:?usage: extract-winsetup.sh <Windows ISO> [destination]}
DEST=${2:-/srv/club/pxe/winsetup}
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }
[ -f "$ISO" ] || { echo "no $ISO" >&2; exit 1; }

MNT=$(mktemp -d)
trap 'umount "$MNT" 2>/dev/null || true; rmdir "$MNT"' EXIT
mount -o loop,ro -t udf "$ISO" "$MNT" 2>/dev/null || mount -o loop,ro "$ISO" "$MNT"

# Names on the ISO differ in case between releases (boot/bcd vs boot/BCD): look them up case-insensitively.
find_one() { # dir name
    found=$(find "$MNT" -maxdepth 2 -ipath "$MNT/$1/$2" -type f | head -n1)
    [ -n "$found" ] || { echo "$1/$2 not found on $ISO — is it a Windows install ISO?" >&2; exit 1; }
    printf '%s' "$found"
}
BCD=$(find_one boot bcd)
SDI=$(find_one boot boot.sdi)
WIM=$(find_one sources boot.wim)
[ -n "$BCD" ] && [ -n "$SDI" ] && [ -n "$WIM" ] || exit 1

install -d -m 755 "$DEST/boot" "$DEST/sources"
cp "$BCD" "$DEST/boot/bcd.partial" && mv "$DEST/boot/bcd.partial" "$DEST/boot/bcd"
cp "$SDI" "$DEST/boot/boot.sdi.partial" && mv "$DEST/boot/boot.sdi.partial" "$DEST/boot/boot.sdi"
cp "$WIM" "$DEST/sources/boot.wim.partial" && mv "$DEST/sources/boot.wim.partial" "$DEST/sources/boot.wim"
chmod 644 "$DEST/boot/bcd" "$DEST/boot/boot.sdi" "$DEST/sources/boot.wim"
ls -la "$DEST/boot" "$DEST/sources"
echo "OK: Windows Setup files in $DEST (diskless master mode with install)"

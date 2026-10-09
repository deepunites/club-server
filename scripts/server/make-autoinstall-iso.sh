#!/usr/bin/env bash
# Builds an Ubuntu Server ISO that installs the club server VM without any keyboard input (autoinstall).
# The original ISO is not modified. Network is static, login only by the given SSH key (no password at all),
# passwordless sudo for the admin user. The VM powers off after the install: detach the ISO (or put the disk first
# in the boot order) before starting it again, otherwise the installer would run again.
#
# Usage:
#   scripts/server/make-autoinstall-iso.sh SRC.iso OUT.iso HOSTNAME IP/PREFIX GATEWAY DNS[,DNS] SSH_PUBKEY_FILE [USER]
# Example:
#   scripts/server/make-autoinstall-iso.sh ubuntu-26.04.1-live-server-amd64.iso club-server-auto.iso \
#       club-server 192.168.0.51/24 192.168.0.1 192.168.0.1,1.1.1.1 ~/.ssh/club_stand.pub clubadmin
set -euo pipefail

if [ $# -lt 7 ]; then
    sed -n '2,13p' "$0"
    exit 2
fi

SRC=$1 OUT=$2 HOST=$3 ADDR=$4 GW=$5 DNS=$6 PUBKEY_FILE=$7 ADMIN=${8:-clubadmin}
command -v xorriso >/dev/null || { echo "xorriso is required" >&2; exit 1; }
[ -f "$SRC" ] || { echo "no $SRC" >&2; exit 1; }
[ -e "$OUT" ] && { echo "$OUT already exists" >&2; exit 1; }
PUBKEY=$(head -n1 "$PUBKEY_FILE")
case "$PUBKEY" in ssh-ed25519\ *|ssh-rsa\ *|ecdsa-sha2-*) ;; *) echo "$PUBKEY_FILE is not an SSH public key" >&2; exit 1 ;; esac
case "$ADDR" in */*) ;; *) echo "IP must be in CIDR form, e.g. 192.168.0.51/24" >&2; exit 1 ;; esac

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/nocloud"

DNS_YAML=$(printf '%s' "$DNS" | tr ',' '\n' | sed 's/^/            - /')

cat > "$WORK/nocloud/user-data" <<EOF
#cloud-config
autoinstall:
  version: 1
  interactive-sections: []
  refresh-installer:
    update: false
  locale: en_US.UTF-8
  keyboard:
    layout: us
  timezone: Asia/Tashkent
  network:
    version: 2
    ethernets:
      primary:
        match:
          name: "en*"
        set-name: lan0
        dhcp4: false
        dhcp6: false
        addresses:
          - $ADDR
        routes:
          - to: default
            via: $GW
        nameservers:
          addresses:
$DNS_YAML
  apt:
    primary:
      - arches: [default]
        uri: http://archive.ubuntu.com/ubuntu
  storage:
    layout:
      name: lvm
      sizing-policy: all
  ssh:
    install-server: true
    allow-pw: false
  packages:
    - curl
    - qemu-guest-agent
  user-data:
    hostname: $HOST
    users:
      - name: $ADMIN
        groups: [sudo]
        shell: /bin/bash
        lock_passwd: true
        sudo: "ALL=(ALL) NOPASSWD:ALL"
        ssh_authorized_keys:
          - $PUBKEY
  shutdown: poweroff
EOF
: > "$WORK/nocloud/meta-data"

# Kernel command line: start the autoinstall from the ISO's own nocloud directory, without the yes/no prompt.
xorriso -osirrox on -indev "$SRC" -extract /boot/grub/grub.cfg "$WORK/grub.cfg" >/dev/null 2>&1
chmod u+w "$WORK/grub.cfg"
grep -q -- '/casper/vmlinuz' "$WORK/grub.cfg" || { echo "unexpected grub.cfg in $SRC" >&2; exit 1; }
sed -i -E 's|^([[:space:]]*linux[[:space:]]+/casper/vmlinuz)(.*)---|\1 autoinstall ds=nocloud\\;s=/cdrom/nocloud/\2---|' "$WORK/grub.cfg"
sed -i -E 's/^set timeout=.*/set timeout=3/' "$WORK/grub.cfg"
grep -q 'autoinstall ds=nocloud' "$WORK/grub.cfg" || { echo "could not patch grub.cfg" >&2; exit 1; }

xorriso -indev "$SRC" -outdev "$OUT" \
    -map "$WORK/grub.cfg" /boot/grub/grub.cfg \
    -map "$WORK/nocloud" /nocloud \
    -boot_image any replay >/dev/null 2>&1

xorriso -osirrox on -indev "$OUT" -extract /nocloud/user-data "$WORK/check" >/dev/null 2>&1
cmp -s "$WORK/check" "$WORK/nocloud/user-data" || { echo "user-data missing in $OUT" >&2; exit 1; }
echo "OK: $OUT — $HOST $ADDR via $GW, user $ADMIN (SSH key only), powers off after install"

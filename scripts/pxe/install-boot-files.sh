#!/bin/sh
# Файлы подписанной цепочки загрузки для перезаливки (docs/imaging.md), от root:
#   TFTP:  ipxe-shim.efi (shim iPXE, подписан Microsoft UEFI CA 2011) + ipxe.efi (подписан iPXE Secure Boot CA)
#          + undionly.kpxe (BIOS) — из официального релиза iPXE;
#   HTTP:  wimboot (подписан Microsoft UEFI CA 2011 и 2023) — в Imaging:PxeRoot.
# Версии и sha256 закреплены: другой файл не установится. Повторный запуск безопасен (замена атомарная).
# Переменные: TFTP_ROOT (/srv/tftp), PXE_ROOT (/srv/club/pxe); IPXE_BUNDLE_URL, WIMBOOT_URL — зеркало (в т. ч. file://).
set -eu

IPXE_VERSION=v2.0.0
IPXE_SHA256=01a526d4cc791fc30362259c609d6c506cc64a7bdff51b9a5eb788354e17eee1
WIMBOOT_VERSION=v2.9.0
WIMBOOT_SHA256=5f067ccdc4d084d5bf77b6c853bd0f8402dfc2b4cd1b103d358993ae97fae8e3

TFTP_ROOT=${TFTP_ROOT:-/srv/tftp}
PXE_ROOT=${PXE_ROOT:-/srv/club/pxe}
IPXE_BUNDLE_URL=${IPXE_BUNDLE_URL:-https://github.com/ipxe/ipxe/releases/download/$IPXE_VERSION/ipxeboot.tar.gz}
WIMBOOT_URL=${WIMBOOT_URL:-https://github.com/ipxe/wimboot/releases/download/$WIMBOOT_VERSION/wimboot}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

fetch() { # url file sha256
    curl -fsSL --retry 3 -o "$2" "$1"
    echo "$3  $2" | sha256sum -c --quiet - || { echo "sha256 mismatch for $1" >&2; exit 1; }
}

install_file() { # source directory name
    mkdir -p "$2"
    cp -L "$1" "$2/.$3.tmp"
    chmod 0644 "$2/.$3.tmp"
    mv -f "$2/.$3.tmp" "$2/$3"
    echo "installed $2/$3"
}

fetch "$IPXE_BUNDLE_URL" "$WORK/ipxeboot.tar.gz" "$IPXE_SHA256"
fetch "$WIMBOOT_URL" "$WORK/wimboot" "$WIMBOOT_SHA256"
tar -xzf "$WORK/ipxeboot.tar.gz" -C "$WORK"

# Имя shim важно: он загружает iPXE с тем же именем без «-shim» (ipxe-shim.efi → ipxe.efi) из того же каталога TFTP.
install_file "$WORK/ipxeboot/x86_64-sb/ipxe-shim.efi" "$TFTP_ROOT" ipxe-shim.efi
install_file "$WORK/ipxeboot/x86_64-sb/ipxe.efi" "$TFTP_ROOT" ipxe.efi
install_file "$WORK/ipxeboot/x86_64/undionly.kpxe" "$TFTP_ROOT" undionly.kpxe
install_file "$WORK/wimboot" "$PXE_ROOT" wimboot
echo "iPXE $IPXE_VERSION, wimboot $WIMBOOT_VERSION: done. Check the panel: Windows images -> network boot files."

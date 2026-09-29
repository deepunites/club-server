#!/bin/sh
# Внутренний CA клуба и сертификаты для TrueNAS и сервера клуба (TrueNAS 25.10 свой CA больше не создаёт).
# Запуск на сервере клуба от root:  sh make-club-ca.sh <IP сервера клуба> <IP TrueNAS>
# Результат в /etc/club-server/tls (или TLS_DIR):
#   club-ca.crt            — CA: в настройки сервера (TrueNas:CaCertificatePath) и на ПК помощнику
#   server.crt/server.key  — HTTPS сервера клуба (панель и API помощника)
#   truenas.crt/truenas.key — импортировать в TrueNAS и выбрать сертификатом веб-интерфейса
# Повторный запуск не пересоздаёт CA (иначе придётся раздать его заново), только недостающие сертификаты.
set -eu
SERVER_IP=${1:?usage: make-club-ca.sh <club server IP> <TrueNAS IP>}
TRUENAS_IP=${2:?usage: make-club-ca.sh <club server IP> <TrueNAS IP>}
DIR=${TLS_DIR:-/etc/club-server/tls}
DAYS_CA=3650
DAYS_CERT=825
mkdir -p "$DIR"
chmod 755 "$DIR"
cd "$DIR"

if [ ! -f club-ca.key ]; then
    openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days "$DAYS_CA" \
        -subj "/CN=Club internal CA" -keyout club-ca.key -out club-ca.crt \
        -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
    chmod 600 club-ca.key
    echo "created CA: $DIR/club-ca.crt"
fi

issue() { # name ip
    [ -f "$1.crt" ] && { echo "exists: $DIR/$1.crt"; return; }
    openssl req -newkey rsa:2048 -sha256 -nodes -subj "/CN=$2" -keyout "$1.key" -out "$1.csr"
    printf 'subjectAltName=IP:%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nbasicConstraints=CA:FALSE\n' "$2" > "$1.ext"
    openssl x509 -req -in "$1.csr" -CA club-ca.crt -CAkey club-ca.key -CAcreateserial -days "$DAYS_CERT" -sha256 -extfile "$1.ext" -out "$1.crt"
    rm -f "$1.csr" "$1.ext"
    chmod 640 "$1.key"
    echo "issued: $DIR/$1.crt for $2"
}

issue server "$SERVER_IP"
issue truenas "$TRUENAS_IP"
# Ключ HTTPS читает служба сервера клуба (пользователь clubsrv).
chgrp clubsrv server.key 2>/dev/null || echo "note: user clubsrv not found yet; run: chgrp clubsrv $DIR/server.key"
openssl verify -CAfile club-ca.crt server.crt truenas.crt

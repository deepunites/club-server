#!/bin/sh
# Настоящая Kea в отдельном сетевом пространстве без root: unshare -r -n sh run.sh <kea.conf> <mac,arch[,userclass]>...
# Сервер слушает veth v0 (192.168.77.1/24), зонд шлёт DHCPDISCOVER с v1 и печатает ответы JSON.
# Окружение: KEA_DHCP4 — бинарник; LD_LIBRARY_PATH и KEA_HOOKS_PATH — если Kea не установлена в систему.
set -e
D=$(cd "$(dirname "$0")" && pwd)
CONF=$1; shift
ip link set lo up
ip link add v0 type veth peer name v1
ip link set v0 up; ip link set v1 up
ip addr add 192.168.77.1/24 dev v0
R=$(mktemp -d)
export KEA_PIDFILE_DIR=$R KEA_LOCKFILE_DIR=$R KEA_DHCP_DATA_DIR=$R KEA_LOG_FILE_DIR=$R KEA_CONTROL_SOCKET_DIR=$R
"$KEA_DHCP4" -c "$CONF" > "$R/kea.log" 2>&1 &
KEA=$!
trap 'kill $KEA 2>/dev/null || true; cat "$R/kea.log" >&2 || true; rm -rf "$R"' EXIT
sleep 2
python3 "$D/dhcp_probe.py" v1 "$@"

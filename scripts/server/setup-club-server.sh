#!/usr/bin/env bash
# Sets up the club server on a fresh Ubuntu Server 26.04 (from make-autoinstall-iso.sh), run as root:
#   sudo bash setup-club-server.sh <club server IP> <TrueNAS IP> <club-server-*-linux-x64.tar.gz> <dir with deploy/ and scripts/>
# What it does (each step is safe to repeat):
#   PostgreSQL, service user clubsrv, database club; club-server in /opt/club-server; club CA and certificates
#   (make-club-ca.sh); /etc/club-server/club-server.env with generated Panel__AdminToken and Auth__ClubApiKey (never
#   printed — read them on the server when needed); systemd unit; Kea 3.0 with its database, grants and the AppArmor
#   rule (service masked: DHCP stays with the router until the pilot); TFTP + iPXE/wimboot (install-boot-files.sh,
#   pinned sha256); Samba share "incoming" for install.wim / WinPE uploads (user clubimg, password set by a person:
#   sudo smbpasswd -a clubimg).
# Not done here (needs TrueNAS first): TrueNas__ApiKey, Library__Enabled, Imaging__Enabled — see docs/install/club.md.
set -euo pipefail

SERVER_IP=${1:?usage: setup-club-server.sh <server IP> <TrueNAS IP> <server tar.gz> <package dir>}
TRUENAS_IP=${2:?}
TARBALL=${3:?}
PKG=${4:?}
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }
[ -f "$TARBALL" ] || { echo "no $TARBALL" >&2; exit 1; }
for f in deploy/club-server.service deploy/club-server.env.example scripts/server/make-club-ca.sh scripts/pxe/install-boot-files.sh; do
    [ -f "$PKG/$f" ] || { echo "no $PKG/$f" >&2; exit 1; }
done
export DEBIAN_FRONTEND=noninteractive
step() { echo; echo "=== $*"; }

step "1/8 packages (Kea and TFTP masked first: nothing serves DHCP/PXE before we say so)"
systemctl mask kea-dhcp4-server kea-dhcp6-server >/dev/null 2>&1 || true
apt-get update -q
apt-get install -y -q postgresql kea-dhcp4-server kea-admin tftpd-hpa samba openssl curl >/dev/null
systemctl disable --now nmbd >/dev/null 2>&1 || true

step "2/8 service user and database"
id clubsrv >/dev/null 2>&1 || useradd --system --home /var/lib/club-server --shell /usr/sbin/nologin clubsrv
install -d -o clubsrv -g clubsrv -m 750 /var/lib/club-server
cd /tmp
sudo -u postgres psql -X -tAc "SELECT 1 FROM pg_roles WHERE rolname='clubsrv'" | grep -q 1 || sudo -u postgres createuser clubsrv
sudo -u postgres psql -X -tAc "SELECT 1 FROM pg_database WHERE datname='club'" | grep -q 1 || sudo -u postgres createdb -O clubsrv club

step "3/8 club-server and certificates"
install -d -m 755 /opt/club-server /etc/club-server
if [ -d /opt/club-server/Club.Server ] || [ -f /opt/club-server/Club.Server ]; then
    rm -rf /opt/club-server.prev && cp -a /opt/club-server /opt/club-server.prev
fi
rm -rf /opt/club-server && install -d -m 755 /opt/club-server
tar -xzf "$TARBALL" -C /opt/club-server
sh "$PKG/scripts/server/make-club-ca.sh" "$SERVER_IP" "$TRUENAS_IP"

step "4/8 settings (/etc/club-server/club-server.env)"
ENV=/etc/club-server/club-server.env
if [ ! -f "$ENV" ]; then
    cp "$PKG/deploy/club-server.env.example" "$ENV"
    sed -i "s|^Panel__AdminToken=.*|Panel__AdminToken=$(openssl rand -hex 24)|" "$ENV"
    sed -i "s|^Auth__ClubApiKey=.*|Auth__ClubApiKey=$(openssl rand -hex 16)|" "$ENV"
fi
sed -i "s|^TrueNas__Host=.*|TrueNas__Host=$TRUENAS_IP|" "$ENV"
sed -i "s|^Library__PortalAddress=.*|Library__PortalAddress=$TRUENAS_IP:3260|" "$ENV"
sed -i "s|^Imaging__PublicBaseUrl=.*|Imaging__PublicBaseUrl=http://$SERVER_IP:5080|" "$ENV"
sed -i "s|^Kea__Enabled=.*|Kea__Enabled=true|" "$ENV"
chown root:clubsrv "$ENV" && chmod 640 "$ENV"
grep -q "^Panel__AdminToken=ЗАМЕНИТЬ" "$ENV" && { echo "Panel__AdminToken not generated" >&2; exit 1; }
install -m 644 "$PKG/deploy/club-server.service" /etc/systemd/system/club-server.service
systemctl daemon-reload

step "5/8 Kea database, grants, AppArmor (service stays masked)"
sudo -u postgres psql -X -tAc "SELECT 1 FROM pg_roles WHERE rolname='_kea'" | grep -q 1 || sudo -u postgres createuser _kea
sudo -u postgres psql -X -tAc "SELECT 1 FROM pg_database WHERE datname='kea'" | grep -q 1 || sudo -u postgres createdb -O _kea kea
if ! sudo -u _kea kea-admin db-version pgsql -h /var/run/postgresql -u _kea -n kea >/dev/null 2>&1; then
    sudo -u _kea kea-admin db-init pgsql -h /var/run/postgresql -u _kea -n kea | tail -1
fi
sudo -u _kea psql -X -q -d kea <<'SQL'
GRANT CONNECT ON DATABASE kea TO clubsrv;
GRANT SELECT ON schema_version TO clubsrv;
GRANT SELECT, INSERT, UPDATE, DELETE ON hosts TO clubsrv;
GRANT USAGE ON SEQUENCE hosts_host_id_seq TO clubsrv;
SQL
LOCAL=/etc/apparmor.d/local/usr.sbin.kea-dhcp4
grep -q "PGSQL" "$LOCAL" 2>/dev/null || printf '%s\n' '# club-server: Kea hosts-database in PostgreSQL over the unix socket' '/run/postgresql/.s.PGSQL.* rw,' >> "$LOCAL"
apparmor_parser -r /etc/apparmor.d/usr.sbin.kea-dhcp4

step "6/8 TFTP and iPXE/wimboot (pinned sha256)"
install -d -m 755 /srv/tftp /srv/club/pxe
sed -i 's|^TFTP_DIRECTORY=.*|TFTP_DIRECTORY="/srv/tftp"|; s|^TFTP_OPTIONS=.*|TFTP_OPTIONS="--secure"|' /etc/default/tftpd-hpa
systemctl restart tftpd-hpa
TFTP_ROOT=/srv/tftp PXE_ROOT=/srv/club/pxe sh "$PKG/scripts/pxe/install-boot-files.sh"

step "7/8 image upload share (Samba, user clubimg; password: sudo smbpasswd -a clubimg)"
id clubimg >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin --groups clubsrv clubimg
install -d -o clubsrv -g clubsrv -m 2770 /srv/club/images /srv/club/images/incoming
install -d -m 755 /srv/club/winsetup
[ -f /etc/samba/smb.conf.orig ] || cp /etc/samba/smb.conf /etc/samba/smb.conf.orig
NET=$(echo "$SERVER_IP" | cut -d. -f1-3).0/24
cat > /etc/samba/smb.conf <<CONF
# club-server: only the upload share (install.wim from the golden VM, WinPE from a PC)
[global]
   workgroup = WORKGROUP
   server string = club-server
   server role = standalone server
   bind interfaces only = no
   hosts allow = 127.0.0.1 $NET
   hosts deny = 0.0.0.0/0
   map to guest = never
   server min protocol = SMB2_10
   load printers = no
   printing = bsd
   printcap name = /dev/null
   disable spoolss = yes
   log file = /var/log/samba/log.%m
   max log size = 1000

[incoming]
   path = /srv/club/images/incoming
   valid users = clubimg
   read only = no
   force group = clubsrv
   create mask = 0660
   directory mask = 2770

# Windows ISO for the diskless master install (extract-winsetup.sh mounts it here and creates the user winsetup)
[winsetup]
   path = /srv/club/winsetup
   valid users = winsetup
   read only = yes
   browseable = no
CONF
testparm -s >/dev/null 2>&1
systemctl restart smbd

step "8/8 club-server service"
# restart, not just start: on a re-run with a new tarball the old process would keep running from deleted files
systemctl enable club-server
systemctl restart club-server
for i in $(seq 1 30); do
    curl -fs http://127.0.0.1:5080/health >/dev/null 2>&1 && break
    [ "$i" = 30 ] && { echo "club-server is not healthy after 60 s: journalctl -u club-server -n 50" >&2; exit 1; }
    sleep 2
done
systemctl is-active club-server
curl -s http://127.0.0.1:5080/health; echo
curl -s --cacert /etc/club-server/tls/club-ca.crt "https://$SERVER_IP:5443/health"; echo
echo
echo "Done. Panel: https://$SERVER_IP:5443/panel/ — token: sudo grep ^Panel__AdminToken $ENV"
echo "Next: TrueNAS API key into $ENV (TrueNas__ApiKey), then Library__Enabled=true; Kea stays masked until the pilot."

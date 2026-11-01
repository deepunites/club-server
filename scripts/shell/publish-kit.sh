#!/usr/bin/env bash
# Выкладывает набор шелла ClubShell на сервер клуба (VM 192.168.1.201), откуда его берёт get-shell-kit.ps1:
#   http://192.168.1.201:5080/pxe/v1/files/tools/shell-kit/<имя>  = /srv/club/pxe/tools/shell-kit/<имя>
#     все scripts/shell/*.ps1 и *.cmd, кроме get-shell-kit.ps1, + ClubShellSetup-1.0.18.exe + SHA256SUMS (формат
#     sha256sum, только имена файлов — get-shell-kit их так и проверяет);
#   http://192.168.1.201:5080/pxe/v1/files/tools/<имя>             = /srv/club/pxe/tools/<имя>
#     get-shell-kit.ps1 (сама команда irm | iex), seal-default-profile.ps1 и disable-autoplay.ps1 (их -Undo и запуск
#     по сети — в шапках и в shell-uninstall).
# Запуск на машине разработки (не на VM): scripts/shell/publish-kit.sh. Повторный запуск безопасен — файлы заменяются.
# .ps1 и .cmd в выкладке — с CRLF (в репозитории концы строк разные; cmd.exe с LF ломается на метках). Копия в
# репозитории не меняется; enable-kiosk-autologon.ps1 (BOM, CRLF) выходит байт в байт как у разработчика шелла.
# Установщик сверяется с SHA256 релиза v1.0.18 до загрузки. Загрузка — во временную папку на VM, установка — sudo -n
# (root:root, файлы 0644, папка 0755: сервер клуба читает их от своего пользователя), SHA256SUMS — последним, чтобы
# get-shell-kit не увидел список раньше файлов. В конце sha256sum установленных файлов на VM сверяется с локальным
# списком, а SHA256SUMS — ещё и по HTTP; любое расхождение — ошибка.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
SETUP_EXE=${SETUP_EXE:-/home/deep/club-dl/clubshell-1.0.18/ClubShellSetup-1.0.18.exe}
SETUP_SHA256=1d23dee18e17be03a19801cd2d7cd1f8c8fde7b8d4e83a2b5ba6785eaf46a1cf
VM=clubadmin@192.168.1.201
SSH_OPTS=(-i "$HOME/.ssh/club_stand" -o BatchMode=yes -o "UserKnownHostsFile=$HOME/.ssh/known_hosts_club")
REMOTE_TOOLS=/srv/club/pxe/tools
REMOTE_KIT=$REMOTE_TOOLS/shell-kit
HTTP_KIT=http://192.168.1.201:5080/pxe/v1/files/tools/shell-kit
# Файлы прямо в tools/ (кроме набора).
TOOLS_FILES=(get-shell-kit.ps1 seal-default-profile.ps1 disable-autoplay.ps1)

die() { echo "ОШИБКА: $*" >&2; exit 1; }

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/kit" "$stage/tools"

# --- установщик --------------------------------------------------------------------------------------------------------
[ -f "$SETUP_EXE" ] || die "нет $SETUP_EXE"
got=$(sha256sum "$SETUP_EXE" | cut -d' ' -f1)
[ "$got" = "$SETUP_SHA256" ] || die "$SETUP_EXE: SHA256 $got, у релиза v1.0.18 — $SETUP_SHA256"
cp "$SETUP_EXE" "$stage/kit/"

# --- скрипты набора ----------------------------------------------------------------------------------------------------
shopt -s nullglob
for f in "$HERE"/*.ps1 "$HERE"/*.cmd; do
  name=$(basename "$f")
  [ "$name" = get-shell-kit.ps1 ] && continue
  cp "$f" "$stage/kit/$name"
done
shopt -u nullglob
for name in "${TOOLS_FILES[@]}"; do
  [ -f "$HERE/$name" ] || die "нет $HERE/$name"
  cp "$HERE/$name" "$stage/tools/$name"
done
for name in shell-install.ps1 shell-preflight.ps1 kiosk-on.ps1 enable-kiosk-autologon.ps1 seal-default-profile.ps1 disable-autoplay.ps1 install-webview2.ps1; do
  [ -f "$stage/kit/$name" ] || die "в наборе нет $name"
done

# CRLF: сначала убрать CR, потом добавить — повторное применение ничего не меняет. LC_ALL=C — побайтово (BOM не трогается).
for f in "$stage"/kit/*.ps1 "$stage"/kit/*.cmd "$stage"/tools/*.ps1; do
  LC_ALL=C sed -i 's/\r$//; s/$/\r/' "$f"
done
cmp -s "$HERE/enable-kiosk-autologon.ps1" "$stage/kit/enable-kiosk-autologon.ps1" \
  || die "enable-kiosk-autologon.ps1 изменился при переводе в CRLF (в репозитории он должен быть байт в байт из cs-src, CRLF)"

# Имена — только простые (get-shell-kit отвергает остальные).
for f in "$stage"/kit/* "$stage"/tools/*; do
  [[ $(basename "$f") =~ ^[A-Za-z0-9._-]+$ ]] || die "имя не подходит для набора: $(basename "$f")"
done

(cd "$stage/kit" && LC_ALL=C sha256sum -- *) > "$stage/SHA256SUMS"
mv "$stage/SHA256SUMS" "$stage/kit/SHA256SUMS"
# В порядке TOOLS_FILES — так же их перечисляет сверка на VM ниже.
(cd "$stage/tools" && LC_ALL=C sha256sum -- "${TOOLS_FILES[@]}") > "$stage/tools.sha256"
echo "== набор ($(($(wc -l < "$stage/kit/SHA256SUMS"))) файлов):"
cat "$stage/kit/SHA256SUMS"
echo "== tools/:"
cat "$stage/tools.sha256"

# --- загрузка и установка --------------------------------------------------------------------------------------------
remote_tmp=$(ssh "${SSH_OPTS[@]}" "$VM" 'mktemp -d /tmp/shell-kit.XXXXXXXX')
[[ $remote_tmp =~ ^/tmp/shell-kit\.[A-Za-z0-9]+$ ]] || die "mktemp на VM вернул '$remote_tmp'"
# Оборвался scp (сеть, Ctrl+C, место в /tmp) или проверка ниже — временная папка на VM всё равно удаляется. После
# установки её удаляет сам сеанс установки, и ловушка снова только локальная.
trap 'rm -rf "$stage"; ssh "${SSH_OPTS[@]}" "$VM" "rm -rf $remote_tmp" || true' EXIT
echo "== загрузка в $VM:$remote_tmp"
scp "${SSH_OPTS[@]}" -q -r "$stage/kit" "$stage/tools" "$VM:$remote_tmp/"
# Папка tools/ уже есть (её отдаёт сервер клуба) — её права не трогаем: install -d поменял бы владельца и режим.
# Временная папка на VM удаляется и при ошибке установки (ловушка в самом сеансе и локальная выше).
# shellcheck disable=SC2029  # пути раскрываются здесь намеренно
ssh "${SSH_OPTS[@]}" "$VM" "set -eu
  trap 'rm -rf $remote_tmp' EXIT
  [ -d '$REMOTE_TOOLS' ] || { echo 'нет $REMOTE_TOOLS на VM' >&2; exit 1; }
  sudo -n install -d -m 0755 -o root -g root '$REMOTE_KIT'
  for f in '$remote_tmp'/kit/*; do
    [ \"\$(basename \"\$f\")\" = SHA256SUMS ] && continue
    sudo -n install -m 0644 -o root -g root \"\$f\" '$REMOTE_KIT/'
  done
  sudo -n install -m 0644 -o root -g root '$remote_tmp/kit/SHA256SUMS' '$REMOTE_KIT/SHA256SUMS'
  sudo -n install -m 0644 -o root -g root '$remote_tmp'/tools/* '$REMOTE_TOOLS/'"
trap 'rm -rf "$stage"' EXIT

# --- сверка ------------------------------------------------------------------------------------------------------------
kit_names=$(cut -c67- "$stage/kit/SHA256SUMS" | tr '\n' ' ')
echo "== на VM ($REMOTE_KIT):"
# shellcheck disable=SC2029
remote_kit=$(ssh "${SSH_OPTS[@]}" "$VM" "cd '$REMOTE_KIT' && LC_ALL=C sha256sum -- $kit_names SHA256SUMS")
echo "$remote_kit"
# shellcheck disable=SC2029
remote_tools=$(ssh "${SSH_OPTS[@]}" "$VM" "cd '$REMOTE_TOOLS' && LC_ALL=C sha256sum -- ${TOOLS_FILES[*]}")
echo "== на VM ($REMOTE_TOOLS):"
echo "$remote_tools"
local_kit=$(cat "$stage/kit/SHA256SUMS"; (cd "$stage/kit" && LC_ALL=C sha256sum -- SHA256SUMS))
diff <(echo "$local_kit") <(echo "$remote_kit") >&2 || die "файлы набора на VM не совпадают с выкладкой (выше)"
diff "$stage/tools.sha256" <(echo "$remote_tools") >&2 || die "файлы в $REMOTE_TOOLS на VM не совпадают с выкладкой (выше)"
# По HTTP — так, как его увидит get-shell-kit.
http_sums=$(curl -fsS --max-time 20 "$HTTP_KIT/SHA256SUMS") || die "сервер клуба не отдаёт $HTTP_KIT/SHA256SUMS"
diff "$stage/kit/SHA256SUMS" <(printf '%s\n' "$http_sums") >&2 || die "$HTTP_KIT/SHA256SUMS не совпадает с выкладкой"
echo "OK: набор выложен и сверен ($HTTP_KIT/, get-shell-kit.ps1, seal-default-profile.ps1, disable-autoplay.ps1)"

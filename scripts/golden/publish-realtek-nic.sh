#!/usr/bin/env bash
# Выкладывает драйвер Realtek NDIS rt640x64 (RTL8111H, PCI\VEN_10EC&DEV_8168) и install-realtek-nic.ps1 на сервер
# клуба (VM 192.168.1.201), откуда их берёт install-realtek-nic.ps1 на гибридах зоны Mirage (docs/realtek-nic.md):
#   http://192.168.1.201:5080/pxe/v1/files/tools/realtek-nic/<имя>  = /srv/club/pxe/tools/realtek-nic/<имя>
#     rt640x64.inf, каталог (.cat) и все файлы из [SourceDisksFiles] / [SourceDisksFiles.amd64] INF — плоско, байт в
#     байт; install-realtek-nic.ps1 (CRLF) — его локальная копия для задачи-сторожа; SHA256SUMS (формат sha256sum,
#     только имена — install-realtek-nic.ps1 их так и проверяет);
#   http://192.168.1.201:5080/pxe/v1/files/tools/install-realtek-nic.ps1 = /srv/club/pxe/tools/install-realtek-nic.ps1
#     сама команда irm | iex — те же байты, что и копия в наборе.
# Запуск на машине разработки (не на VM):
#   scripts/golden/publish-realtek-nic.sh <папка распакованного пакета драйвера | путь к rt640x64.inf>
# Пакет — MSI realtek_pcielan_w11.zip (Realtek 10.79.50.1003 или новее, например 10.80.50): распаковать и дать папку.
# В нём ищется rt640x64.inf для Win10/Win11 x64; папки WinPE пропускаются. Нашлось несколько — дать путь к нужному INF.
# Версия не зашита: печатается DriverVer из INF, её же скрипт на ПК читает из INF набора.
# Файлы драйвера не меняются (CRLF — только у .ps1: в репозитории LF). Повторный запуск безопасен — файлы заменяются;
# старые файлы прошлой версии на VM остаются, но их нет в SHA256SUMS, и скрипт на ПК их не качает.
# Загрузка — во временную папку на VM, установка — sudo -n (root:root, файлы 0644, папка 0755: сервер клуба читает их от
# своего пользователя), SHA256SUMS набора — после файлов, install-realtek-nic.ps1 в tools/ — последним, чтобы команда
# irm | iex не увидела список раньше файлов. В конце sha256sum на VM сверяется с выкладкой, а по HTTP — каждый файл.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT_NAME=install-realtek-nic.ps1
VM=clubadmin@192.168.1.201
SSH_OPTS=(-i "$HOME/.ssh/club_stand" -o BatchMode=yes -o "UserKnownHostsFile=$HOME/.ssh/known_hosts_club")
REMOTE_TOOLS=/srv/club/pxe/tools
REMOTE_SET=$REMOTE_TOOLS/realtek-nic
HTTP_TOOLS=http://192.168.1.201:5080/pxe/v1/files/tools
HTTP_SET=$HTTP_TOOLS/realtek-nic

die() { echo "ОШИБКА: $*" >&2; exit 1; }

[ $# -eq 1 ] || die "использование: $0 <папка распакованного пакета драйвера | путь к rt640x64.inf>"
src=$1
[ -f "$HERE/$SCRIPT_NAME" ] || die "нет $HERE/$SCRIPT_NAME"

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/set" "$stage/tools"

# --- INF -----------------------------------------------------------------------------------------------------------------
# Путь с папкой WinPE (любой регистр) — драйвер для WinPE, не для установленной Windows.
is_winpe() { [[ ${1,,} =~ (^|/)[^/]*winpe[^/]*/ ]]; }

if [ -f "$src" ]; then
  [ "$(basename "${src,,}")" = rt640x64.inf ] || die "$src — не rt640x64.inf"
  inf=$src
elif [ -d "$src" ]; then
  candidates=()
  while IFS= read -r -d '' f; do
    is_winpe "$f" && { echo "  пропуск (WinPE): $f"; continue; }
    candidates+=("$f")
  done < <(find "$src" -type f -iname 'rt640x64.inf' -print0 | sort -z)
  [ ${#candidates[@]} -gt 0 ] || die "в $src нет rt640x64.inf (кроме папок WinPE)"
  if [ ${#candidates[@]} -gt 1 ]; then
    printf '  %s\n' "${candidates[@]}" >&2
    die "несколько rt640x64.inf (выше): дайте путь к нужному INF (Win10/Win11 x64, не WinPE)"
  fi
  inf=${candidates[0]}
else
  die "нет $src"
fi
inf=$(realpath "$inf")
is_winpe "$inf" && die "$inf лежит в папке WinPE: нужен INF для Win10/Win11 x64"
infdir=$(dirname "$inf")
echo "== INF: $inf"

# Текст INF в UTF-8 без CR (INF бывают в UTF-16 LE с BOM и с UTF-8 BOM) — только для разбора; в набор идёт исходный файл.
bom=$(head -c2 "$inf" | od -An -tx1 | tr -d ' \n')
if [ "$bom" = fffe ]; then
  iconv -f UTF-16 -t UTF-8 "$inf" > "$stage/inf.txt"
else
  LC_ALL=C sed '1s/^\xEF\xBB\xBF//' "$inf" > "$stage/inf.txt"
fi
LC_ALL=C sed -i 's/\r$//' "$stage/inf.txt"

# Строки секции (имя без учёта регистра; секция может встречаться несколько раз), без комментариев и пустых строк.
inf_section() {
  awk -v want="${1,,}" '
    { line = $0; sub(/;.*/, "", line); gsub(/^[ \t]+|[ \t]+$/, "", line) }
    line ~ /^\[.*\]$/ { name = tolower(substr(line, 2, length(line) - 2)); gsub(/^[ \t]+|[ \t]+$/, "", name); insec = (name == want); next }
    insec && line != "" { print line }
  ' "$stage/inf.txt"
}
# Значение после «=», без пробелов по краям и кавычек.
value_of() { sed 's/^[^=]*=//; s/^[[:space:]]*//; s/[[:space:]]*$//; s/^"//; s/"$//'; }

grep -qi 'VEN_10EC&DEV_8168' "$stage/inf.txt" || die "$inf не перечисляет PCI\\VEN_10EC&DEV_8168"
inf_section Manufacturer | grep -qi 'ntamd64' || die "$inf: в [Manufacturer] нет NTamd64 — не драйвер для x64"
driver_ver=$(inf_section Version | grep -i '^DriverVer[[:space:]]*=' | head -n1 | value_of || true)
[[ $driver_ver =~ ^[0-9]{1,2}/[0-9]{1,2}/[0-9]{4}[[:space:]]*,[[:space:]]*[0-9]+(\.[0-9]+){1,3}$ ]] \
  || die "$inf: DriverVer в [Version] не читается: '$driver_ver'"
echo "== DriverVer = $driver_ver"
lists_board() { grep -qi "DEV_8168&SUBSYS_$1" "$stage/inf.txt" && echo да || echo нет; }
echo "   платы Mirage в INF: SUBSYS_7C021462 (B450 TOMAHAWK) — $(lists_board 7C021462), SUBSYS_7B791462 (X470 GAMING PLUS MAX) — $(lists_board 7B791462)"

# Плоская раскладка: подпапка в [SourceDisksNames] / [SourceDisksFiles] сломала бы установку из плоского набора.
for sec in SourceDisksNames SourceDisksNames.amd64; do
  while IFS= read -r line; do
    path=$(printf '%s\n' "$line" | value_of | awk -F, '{ p = $4; gsub(/^[ \t"]+|[ \t"]+$/, "", p); print p }')
    case $path in ''|.|\\|.\\) ;; *) die "[$sec] задаёт подпапку '$path' ($line): набор плоский, такой INF не подходит" ;; esac
  done < <(inf_section "$sec")
done
names=()
while IFS= read -r line; do
  catalog=$(printf '%s\n' "$line" | value_of)
  [ -n "$catalog" ] && names+=("$catalog")
done < <(inf_section Version | grep -iE '^CatalogFile(\.ntamd64)?[[:space:]]*=' || true)
[ ${#names[@]} -gt 0 ] || die "$inf: в [Version] нет CatalogFile — неподписанный драйвер не поставится"
for sec in SourceDisksFiles SourceDisksFiles.amd64; do
  while IFS= read -r line; do
    name=$(printf '%s\n' "$line" | sed 's/=.*//; s/^[[:space:]]*//; s/[[:space:]]*$//; s/^"//; s/"$//')
    subdir=$(printf '%s\n' "$line" | value_of | awk -F, '{ p = $2; gsub(/^[ \t"]+|[ \t"]+$/, "", p); print p }')
    [ -z "$subdir" ] || die "[$sec] кладёт $name в подпапку '$subdir': набор плоский, такой INF не подходит"
    names+=("$name")
  done < <(inf_section "$sec")
done
names+=("$(basename "$inf")")
printf '%s\n' "${names[@]}" | grep -qi '\.sys$' || die "$inf: в [SourceDisksFiles*] нет ни одного .sys"

# --- Набор: файлы драйвера байт в байт ----------------------------------------------------------------------------------
declare -A taken=()
for name in "${names[@]}"; do
  [[ $name =~ ^[A-Za-z0-9._-]+$ ]] || die "имя файла из INF не подходит для набора: '$name'"
  key=${name,,}
  [ -n "${taken[$key]:-}" ] && continue
  # Регистр имени в INF и на диске может не совпадать (Windows его не различает).
  mapfile -t hits < <(find "$infdir" -maxdepth 1 -type f -iname "$name")
  [ ${#hits[@]} -eq 1 ] || die "INF ссылается на $name: в $infdir найдено ${#hits[@]} таких файлов"
  real=$(basename "${hits[0]}")
  [[ $real =~ ^[A-Za-z0-9._-]+$ ]] || die "имя файла не подходит для набора: '$real'"
  case ${real,,} in sha256sums|logs|"$SCRIPT_NAME") die "файл драйвера $real совпадает с именем служебного файла набора" ;; esac
  cp -- "${hits[0]}" "$stage/set/$real"
  cmp -s -- "${hits[0]}" "$stage/set/$real" || die "копия $real не совпадает с исходным файлом"
  taken[$key]=$real
done
# Скрипт на ПК ищет INF по имени rt640x64.inf (без учёта регистра).
[ -n "${taken[rt640x64.inf]:-}" ] || die "rt640x64.inf не попал в набор"

# --- install-realtek-nic.ps1: в наборе (копия для сторожа) и в tools/ (команда irm | iex) — одни и те же байты --------
head -c3 "$HERE/$SCRIPT_NAME" | od -An -tx1 | grep -qi 'ef bb bf' && die "$SCRIPT_NAME с BOM: irm в PowerShell 5.1 его не прочтёт"
cp "$HERE/$SCRIPT_NAME" "$stage/tools/$SCRIPT_NAME"
# CRLF: сначала убрать CR, потом добавить — повторное применение ничего не меняет. LC_ALL=C — побайтово.
LC_ALL=C sed -i 's/\r$//; s/$/\r/' "$stage/tools/$SCRIPT_NAME"
cp "$stage/tools/$SCRIPT_NAME" "$stage/set/$SCRIPT_NAME"

(cd "$stage/set" && LC_ALL=C sha256sum -- *) > "$stage/SHA256SUMS"
mv "$stage/SHA256SUMS" "$stage/set/SHA256SUMS"
(cd "$stage/tools" && LC_ALL=C sha256sum -- "$SCRIPT_NAME") > "$stage/tools.sha256"
echo "== набор realtek-nic/ ($(($(wc -l < "$stage/set/SHA256SUMS"))) файлов, DriverVer $driver_ver):"
cat "$stage/set/SHA256SUMS"
echo "== tools/:"
cat "$stage/tools.sha256"

# --- загрузка и установка ----------------------------------------------------------------------------------------------
remote_tmp=$(ssh "${SSH_OPTS[@]}" "$VM" 'mktemp -d /tmp/realtek-nic.XXXXXXXX')
[[ $remote_tmp =~ ^/tmp/realtek-nic\.[A-Za-z0-9]+$ ]] || die "mktemp на VM вернул '$remote_tmp'"
# Оборвался scp (сеть, Ctrl+C, место в /tmp) или проверка ниже — временная папка на VM всё равно удаляется. После
# установки её удаляет сам сеанс установки, и ловушка снова только локальная.
trap 'rm -rf "$stage"; ssh "${SSH_OPTS[@]}" "$VM" "rm -rf $remote_tmp" || true' EXIT
echo "== загрузка в $VM:$remote_tmp"
scp "${SSH_OPTS[@]}" -q -r "$stage/set" "$stage/tools" "$VM:$remote_tmp/"
# Папка tools/ уже есть (её отдаёт сервер клуба) — её права не трогаем: install -d поменял бы владельца и режим.
# Временная папка на VM удаляется и при ошибке установки (ловушка в самом сеансе и локальная выше).
# shellcheck disable=SC2029  # пути раскрываются здесь намеренно
ssh "${SSH_OPTS[@]}" "$VM" "set -eu
  trap 'rm -rf $remote_tmp' EXIT
  [ -d '$REMOTE_TOOLS' ] || { echo 'нет $REMOTE_TOOLS на VM' >&2; exit 1; }
  sudo -n install -d -m 0755 -o root -g root '$REMOTE_SET'
  for f in '$remote_tmp'/set/*; do
    [ \"\$(basename \"\$f\")\" = SHA256SUMS ] && continue
    sudo -n install -m 0644 -o root -g root \"\$f\" '$REMOTE_SET/'
  done
  sudo -n install -m 0644 -o root -g root '$remote_tmp/set/SHA256SUMS' '$REMOTE_SET/SHA256SUMS'
  sudo -n install -m 0644 -o root -g root '$remote_tmp/tools/$SCRIPT_NAME' '$REMOTE_TOOLS/$SCRIPT_NAME'"
trap 'rm -rf "$stage"' EXIT

# --- сверка ------------------------------------------------------------------------------------------------------------
set_names=$(cut -c67- "$stage/set/SHA256SUMS" | tr '\n' ' ')
echo "== на VM ($REMOTE_SET):"
# shellcheck disable=SC2029
remote_set=$(ssh "${SSH_OPTS[@]}" "$VM" "cd '$REMOTE_SET' && LC_ALL=C sha256sum -- $set_names SHA256SUMS")
echo "$remote_set"
# shellcheck disable=SC2029
remote_tools=$(ssh "${SSH_OPTS[@]}" "$VM" "cd '$REMOTE_TOOLS' && LC_ALL=C sha256sum -- $SCRIPT_NAME")
echo "== на VM ($REMOTE_TOOLS):"
echo "$remote_tools"
local_set=$(cat "$stage/set/SHA256SUMS"; (cd "$stage/set" && LC_ALL=C sha256sum -- SHA256SUMS))
diff <(echo "$local_set") <(echo "$remote_set") >&2 || die "файлы набора на VM не совпадают с выкладкой (выше)"
diff "$stage/tools.sha256" <(echo "$remote_tools") >&2 || die "$REMOTE_TOOLS/$SCRIPT_NAME на VM не совпадает с выкладкой (выше)"
# По HTTP — так, как их увидит install-realtek-nic.ps1: список и каждый файл.
http_sums=$(curl -fsS --max-time 20 "$HTTP_SET/SHA256SUMS") || die "сервер клуба не отдаёт $HTTP_SET/SHA256SUMS"
diff "$stage/set/SHA256SUMS" <(printf '%s\n' "$http_sums") >&2 || die "$HTTP_SET/SHA256SUMS не совпадает с выкладкой"
while read -r sum name; do
  got=$(curl -fsS --max-time 60 "$HTTP_SET/$name" | sha256sum | cut -d' ' -f1) || die "сервер клуба не отдаёт $HTTP_SET/$name"
  [ "$got" = "$sum" ] || die "$HTTP_SET/$name: SHA256 $got, в выкладке $sum"
done < "$stage/set/SHA256SUMS"
read -r tools_sum _ < "$stage/tools.sha256"
got=$(curl -fsS --max-time 20 "$HTTP_TOOLS/$SCRIPT_NAME" | sha256sum | cut -d' ' -f1) || die "сервер клуба не отдаёт $HTTP_TOOLS/$SCRIPT_NAME"
[ "$got" = "$tools_sum" ] || die "$HTTP_TOOLS/$SCRIPT_NAME: SHA256 $got, в выкладке $tools_sum"
echo "OK: rt640x64 $driver_ver выложен и сверен ($HTTP_SET/, $HTTP_TOOLS/$SCRIPT_NAME)"
echo "На ПК (Терминал от администратора, вход Club): irm $HTTP_TOOLS/$SCRIPT_NAME | iex"

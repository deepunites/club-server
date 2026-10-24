#!/bin/sh
# Новая версия гибридного образа без пересъёмки: копия install.wim готового образа + файлы из scripts/golden
# (docs/imaging.md). Кладёт в образ:
#   /Windows/Panther/unattend.xml           <- unattend-hybrid.xml (OOBE, учётная запись Club, автовход, FirstLogonCommands)
#   /Windows/Setup/Scripts/SetupComplete.cmd <- SetupComplete.cmd (SYSTEM, до первого входа)
#   /Windows/Setup/Scripts/nvcp/             <- nvcp/Install-NVCP.ps1 + пакет и лицензия Панели управления NVIDIA
#                                               из Display.Driver/NVCPL архива драйвера (gpu-nv-617.42.zip)
# Результат — incoming/<имя>.wim; дальше импорт: POST /images/import {"file":"<имя>.wim","label":...,"index":1}.
# Запуск на сервере от root (образы принадлежат clubsrv):
#   sh patch-hybrid-wim.sh <образ-источник> <имя> <папка с unattend-hybrid.xml, SetupComplete.cmd, nvcp/>
# Пример (h1c, 2026-10-25): sh patch-hybrid-wim.sh h1b-club-admin h1c-club-admin-2026-10-25 /tmp/h1c-stage
set -eu
SRC=/srv/club/images/images/$1/install.wim
NAME=$2
STAGE=$3
IN=/srv/club/images/incoming
NV_ZIP=/srv/club/pxe/tools/drivers/gpu-nv-617.42.zip
NVCP=7e010d7e2de54a439dbd340dd1f61212

[ -f "$SRC" ] || { echo "нет $SRC" >&2; exit 1; }
for f in unattend-hybrid.xml SetupComplete.cmd nvcp/Install-NVCP.ps1; do
  [ -f "$STAGE/$f" ] || { echo "нет $STAGE/$f" >&2; exit 1; }
done
[ ! -e "$IN/$NAME.wim" ] || { echo "уже есть $IN/$NAME.wim" >&2; exit 1; }
# Пути в WIM без учёта регистра, как в Windows (иначе add создаст второй /windows/... рядом с /Windows/...).
export WIMLIB_IMAGEX_IGNORE_CASE=1
# Недоделанная копия (17 ГБ) не остаётся на томе образов при любой ошибке.
trap 'rm -f "$IN/$NAME.wim.partial" "$IN/$NAME.verify.log"' EXIT

python3 - "$NV_ZIP" "$STAGE/nvcp" "$NVCP" <<'PY'
import sys, zipfile
zip_path, dest, nvcp = sys.argv[1:]
with zipfile.ZipFile(zip_path) as z:
    for name in (nvcp + '.appx', nvcp + '_License1.xml'):
        with open(dest + '/' + name, 'wb') as f:
            f.write(z.read('Display.Driver/NVCPL/' + name))
PY

echo "== $NAME из $SRC"
cp --reflink=auto "$SRC" "$IN/$NAME.wim.partial"
wimlib-imagex update "$IN/$NAME.wim.partial" 1 <<CMDS
add $STAGE/unattend-hybrid.xml /Windows/Panther/unattend.xml
add $STAGE/SetupComplete.cmd /Windows/Setup/Scripts/SetupComplete.cmd
add $STAGE/nvcp /Windows/Setup/Scripts/nvcp
CMDS

# Проверка: файлы на месте и совпадают с источником, WIM цел.
for pair in "unattend-hybrid.xml:/Windows/Panther/unattend.xml" "SetupComplete.cmd:/Windows/Setup/Scripts/SetupComplete.cmd" \
            "nvcp/Install-NVCP.ps1:/Windows/Setup/Scripts/nvcp/Install-NVCP.ps1" "nvcp/$NVCP.appx:/Windows/Setup/Scripts/nvcp/$NVCP.appx" \
            "nvcp/${NVCP}_License1.xml:/Windows/Setup/Scripts/nvcp/${NVCP}_License1.xml"; do
  local_file=$STAGE/${pair%%:*}; wim_path=${pair#*:}
  want=$(sha256sum "$local_file" | cut -d' ' -f1)
  got=$(wimlib-imagex extract "$IN/$NAME.wim.partial" 1 "$wim_path" --to-stdout | sha256sum | cut -d' ' -f1)
  [ "$want" = "$got" ] || { echo "не совпал $wim_path" >&2; exit 1; }
  echo "ok $wim_path $want"
done
wimlib-imagex verify "$IN/$NAME.wim.partial" > "$IN/$NAME.verify.log" 2>&1 || { tail -5 "$IN/$NAME.verify.log" >&2; echo "WIM не прошёл проверку" >&2; exit 1; }
tail -1 "$IN/$NAME.verify.log"
chown clubsrv:clubsrv "$IN/$NAME.wim.partial"
chmod 660 "$IN/$NAME.wim.partial"
mv "$IN/$NAME.wim.partial" "$IN/$NAME.wim"
trap - EXIT
rm -f "$IN/$NAME.verify.log"
ls -la "$IN/$NAME.wim"

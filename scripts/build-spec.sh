#!/bin/sh
# docs/diskless-api.yaml → docs/diskless-api.json (для проверки ответов в тестах). Запускать после правки спецификации.
set -eu
ROOT=$(cd "$(dirname "$0")/.." && pwd)
python3 -c 'import json,sys,yaml; json.dump(yaml.safe_load(open(sys.argv[1])), open(sys.argv[2],"w"), ensure_ascii=False, indent=1)' \
  "$ROOT/docs/diskless-api.yaml" "$ROOT/docs/diskless-api.json"
echo "ok: docs/diskless-api.json"

#!/bin/sh
# Копирует собранный контракт из club-contracts и делает JSON-версию для контрактных тестов.
set -eu
SRC=${1:-../club-contracts}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cp "$SRC/openapi/openapi.yaml" "$ROOT/contracts/openapi.yaml"
python3 -c 'import json,sys,yaml; json.dump(yaml.safe_load(open(sys.argv[1])), open(sys.argv[2],"w"), ensure_ascii=False)' \
  "$ROOT/contracts/openapi.yaml" "$ROOT/contracts/openapi.json"
echo "ok: contracts/openapi.yaml, contracts/openapi.json"

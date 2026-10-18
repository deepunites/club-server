#!/bin/sh
# Club backups to the local hdd pool (TrueNAS 25.10.7, run as truenas_admin in the web Shell; safe to re-run):
#   sh club-backup.sh <club-server VM zvol>        e.g. sh club-backup.sh ssd/clubserver-3agcve
# Only TrueNAS-owned bkp-* snapshot chains are replicated, non-recursively, so the club server's own snapshots
# (lib-*, img-*, @clean) and its disposable per-seat clones are never touched:
#   games/club/lib        weekly  Mon 05:00, 1 week on games, 3 weeks on hdd
#   <VM zvol>             daily   04:30,     7 days on ssd, 14 days + Sundays 8 weeks on hdd
# Not backed up: games/club/seats, ssd/club/diskless/seats, games/club/published (rebuilt by the server), ssd/iso.
# The golden image (img-* by name regex) gets its task after the first image publish.
set -eu
VMZVOL=${1:?usage: club-backup.sh <club-server VM zvol, see: zfs list -t volume>}
GIB=1073741824

json_field() { python3 -c "import json,sys; v=json.load(sys.stdin); print($1)"; }

mk() { # dataset quota-GiB
    n=$(midclt call pool.dataset.query "[[\"id\",\"=\",\"$1\"]]" | json_field 'len(v)')
    if [ "$n" = 0 ]; then
        midclt call pool.dataset.create "{\"name\":\"$1\",\"type\":\"FILESYSTEM\",\"quota\":$(($2 * GIB)),\"atime\":\"OFF\"}" >/dev/null
        echo "created $1 (quota $2 GiB)"
    else
        echo "exists  $1"
    fi
}

task_id() { # dataset -> id of its bkp-* periodic snapshot task, or empty
    midclt call pool.snapshottask.query "[[\"dataset\",\"=\",\"$1\"],[\"naming_schema\",\"=\",\"bkp-%Y-%m-%d_%H-%M\"]]" | json_field '(v[0]["id"] if v else "")'
}

repl_id() { # name -> id or empty
    midclt call replication.query "[[\"name\",\"=\",\"$1\"]]" | json_field '(v[0]["id"] if v else "")'
}

[ "$(midclt call pool.dataset.query "[[\"id\",\"=\",\"$VMZVOL\"]]" | json_field 'len(v)')" = 1 ] || { echo "no $VMZVOL" >&2; exit 1; }

mk hdd/backup 1792
mk hdd/backup/games 1434
mk hdd/backup/diskless 160
mk hdd/backup/vm 160

# Games library master: weekly.
LIB=$(task_id games/club/lib)
if [ -z "$LIB" ]; then
    LIB=$(midclt call pool.snapshottask.create '{"dataset":"games/club/lib","recursive":false,"naming_schema":"bkp-%Y-%m-%d_%H-%M","lifetime_value":1,"lifetime_unit":"WEEK","allow_empty":true,"enabled":true,"schedule":{"minute":"0","hour":"5","dom":"*","month":"*","dow":"1","begin":"05:00","end":"05:59"}}' | json_field 'v["id"]')
    echo "snapshot task games/club/lib: id $LIB"
fi
R=$(repl_id "games-lib to hdd")
if [ -z "$R" ]; then
    R=$(midclt call replication.create "{\"name\":\"games-lib to hdd\",\"direction\":\"PUSH\",\"transport\":\"LOCAL\",\"source_datasets\":[\"games/club/lib\"],\"target_dataset\":\"hdd/backup/games/lib\",\"recursive\":false,\"periodic_snapshot_tasks\":[$LIB],\"auto\":true,\"retention_policy\":\"CUSTOM\",\"lifetime_value\":3,\"lifetime_unit\":\"WEEK\",\"hold_pending_snapshots\":true,\"readonly\":\"SET\",\"properties\":true,\"properties_override\":{\"volmode\":\"none\"},\"allow_from_scratch\":false,\"encryption\":false,\"large_block\":true,\"compressed\":true,\"retries\":5,\"enabled\":true}" | json_field 'v["id"]')
    echo "replication games-lib to hdd: id $R"
fi

# Club server VM disk (system, PostgreSQL, settings): daily, crash-consistent.
VM=$(task_id "$VMZVOL")
if [ -z "$VM" ]; then
    VM=$(midclt call pool.snapshottask.create "{\"dataset\":\"$VMZVOL\",\"recursive\":false,\"naming_schema\":\"bkp-%Y-%m-%d_%H-%M\",\"lifetime_value\":7,\"lifetime_unit\":\"DAY\",\"allow_empty\":true,\"enabled\":true,\"schedule\":{\"minute\":\"30\",\"hour\":\"4\",\"dom\":\"*\",\"month\":\"*\",\"dow\":\"*\",\"begin\":\"04:30\",\"end\":\"04:59\"}}" | json_field 'v["id"]')
    echo "snapshot task $VMZVOL: id $VM"
fi
R=$(repl_id "club-server VM to hdd")
if [ -z "$R" ]; then
    R=$(midclt call replication.create "{\"name\":\"club-server VM to hdd\",\"direction\":\"PUSH\",\"transport\":\"LOCAL\",\"source_datasets\":[\"$VMZVOL\"],\"target_dataset\":\"hdd/backup/vm/clubserver\",\"recursive\":false,\"periodic_snapshot_tasks\":[$VM],\"auto\":true,\"retention_policy\":\"CUSTOM\",\"lifetime_value\":14,\"lifetime_unit\":\"DAY\",\"lifetimes\":[{\"schedule\":{\"minute\":\"*\",\"hour\":\"*\",\"dom\":\"*\",\"month\":\"*\",\"dow\":\"7\"},\"lifetime_value\":8,\"lifetime_unit\":\"WEEK\"}],\"hold_pending_snapshots\":true,\"readonly\":\"SET\",\"properties\":true,\"properties_exclude\":[\"refreservation\"],\"properties_override\":{\"volmode\":\"none\"},\"allow_from_scratch\":false,\"encryption\":false,\"retries\":5,\"enabled\":true}" | json_field 'v["id"]')
    echo "replication club-server VM to hdd: id $R"
fi

echo "--- tasks"
midclt call pool.snapshottask.query '[]' | json_field '"\n".join("%s %s keep %s %s, cron min %s hour %s dow %s" % (t["id"], t["dataset"], t["lifetime_value"], t["lifetime_unit"], t["schedule"]["minute"], t["schedule"]["hour"], t["schedule"]["dow"]) for t in v)'
midclt call replication.query '[]' | json_field '"\n".join("%s %s: %s -> %s" % (r["id"], r["name"], r["source_datasets"], r["target_dataset"]) for r in v)'
zfs list -o name,quota,used,avail -r hdd

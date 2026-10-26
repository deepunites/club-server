#!/bin/sh
# Club backups on TrueNAS 25.10.7 (run as truenas_admin in the web Shell). Safe to re-run: datasets and tasks that
# already exist are left as they are, nothing is deleted:
#   sh club-backup.sh <club-server VM zvol>   datasets + snapshot and replication tasks, e.g. ssd/clubserver-3agcve
#   sh club-backup.sh --quotas                only the hdd/backup quotas below, on datasets that exist (no tasks)
# Layout since 2026-10-26 (docs/install/club.md): pool «backup» (mirror 2 x 240 GB SSD) holds the copies of the
# club-server VM and of the diskless golden; pool «hdd» (RAIDZ1 3 x 1 TB) holds the copies of the games library G: and of
# the hybrid images, and the second games library H: itself (hdd/club; not backed up: its games can be downloaded again,
# its lib-* versions give rollback). Only TrueNAS-owned bkp-* snapshot chains (and the golden's img-* versions, by name)
# are replicated, non-recursively, so the club server's own lib-* and @clean snapshots and its per-seat clones are never
# touched:
#   games/club/lib           weekly Mon 05:00, 1 week on games,  3 weeks on hdd          -> hdd/backup/games/lib
#   games/club/images        weekly Mon 05:30, 2 weeks on games, 3 weeks on hdd          -> hdd/backup/images/club-images
#   <VM zvol>                daily 04:30,      7 days on ssd,    14 days + Sundays 8 weeks -> backup/vm/clubserver
#   ssd/club/diskless/win11  img-* (versions the server publishes), daily 05:15, kept as on ssd -> backup/diskless/win11
# Not backed up: games/club/seats, games/club/published, ssd/club/diskless/seats (rebuilt by the server), ssd/iso, hdd/club.
# Quotas on hdd, GiB (split confirmed by the owner 2026-10-26: H: gets 1 TiB, the copies the rest): hdd/backup 720
# (games 560, images 160) next to hdd/club 1024 (H:) = 1744 of ~1790 (1.75 TiB) in the pool, so copies and H: never take
# each other's room. hdd/backup/games holds the full G: replica plus 3 weeks of changes: G: content above ~400 GB makes
# the weekly G: replication fail on quota (TrueNAS alert «Replication games-lib to hdd failed»; check:
# zfs list -o space -r hdd/backup). Then move room from hdd/club to hdd/backup, never both above the pool. The numbers
# are the three variables below.
set -eu
GIB=1073741824
BACKUP_QUOTA=720 # hdd/backup: everything below
GAMES_QUOTA=560  # hdd/backup/games: the G: replica
IMAGES_QUOTA=160 # hdd/backup/images: the hybrid images replica

json_field() { python3 -c "import json,sys; v=json.load(sys.stdin); print($1)"; }

exists() { [ "$(midclt call pool.dataset.query "[[\"id\",\"=\",\"$1\"]]" | json_field 'len(v)')" != 0 ]; }

gib() { echo $(($(zfs get -Hp -o value "$1" "$2") / GIB)); } # property dataset -> GiB

mk() { # dataset [quota-GiB]: created when missing (with the quota); an existing one is left as it is
    if exists "$1"; then
        echo "exists  $1 (quota $(gib quota "$1") GiB, uses $(gib used "$1") GiB)"
    elif [ -n "${2:-}" ]; then
        midclt call pool.dataset.create "{\"name\":\"$1\",\"type\":\"FILESYSTEM\",\"quota\":$(($2 * GIB)),\"atime\":\"OFF\"}" >/dev/null
        echo "created $1 (quota $2 GiB)"
    else
        midclt call pool.dataset.create "{\"name\":\"$1\",\"type\":\"FILESYSTEM\",\"atime\":\"OFF\"}" >/dev/null
        echo "created $1"
    fi
}

quota() { # dataset quota-GiB: set when the dataset has none or a larger one, never below what it uses (ZFS refuses that)
    if ! exists "$1"; then
        echo "missing $1: run sh club-backup.sh <VM zvol> first" >&2
        return
    fi
    q=$(zfs get -Hp -o value quota "$1")
    u=$(zfs get -Hp -o value used "$1")
    if [ "$q" != 0 ] && [ "$q" -le $(($2 * GIB)) ]; then
        echo "kept    $1 (quota $((q / GIB)) GiB, uses $((u / GIB)) GiB)"
    elif [ "$u" -ge $(($2 * GIB)) ]; then
        echo "kept    $1: uses $((u / GIB)) GiB, quota $2 GiB NOT set - free space first" >&2
    else
        midclt call pool.dataset.update "$1" "{\"quota\":$(($2 * GIB))}" >/dev/null
        echo "quota   $1 set to $2 GiB (uses $((u / GIB)) GiB)"
    fi
}

task_id() { # dataset -> id of its bkp-* periodic snapshot task, or empty
    midclt call pool.snapshottask.query "[[\"dataset\",\"=\",\"$1\"],[\"naming_schema\",\"=\",\"bkp-%Y-%m-%d_%H-%M\"]]" | json_field '(v[0]["id"] if v else "")'
}

repl_id() { # name -> id or empty
    midclt call replication.query "[[\"name\",\"=\",\"$1\"]]" | json_field '(v[0]["id"] if v else "")'
}

if [ "${1:-}" = "--quotas" ]; then
    quota hdd/backup "$BACKUP_QUOTA"
    quota hdd/backup/games "$GAMES_QUOTA"
    quota hdd/backup/images "$IMAGES_QUOTA"
    zfs list -o name,quota,used,avail -r hdd
    exit 0
fi

VMZVOL=${1:?usage: club-backup.sh <club-server VM zvol, see: zfs list -t volume> | --quotas}
exists "$VMZVOL" || { echo "no $VMZVOL" >&2; exit 1; }

mk hdd/backup "$BACKUP_QUOTA"
mk hdd/backup/games "$GAMES_QUOTA"
mk hdd/backup/images "$IMAGES_QUOTA"
mk backup/vm
mk backup/diskless

# Games library master G:: weekly.
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

# Hybrid images (WIM versions on the images zvol of the club server): weekly.
if exists games/club/images; then
    IMG=$(task_id games/club/images)
    if [ -z "$IMG" ]; then
        IMG=$(midclt call pool.snapshottask.create '{"dataset":"games/club/images","recursive":false,"naming_schema":"bkp-%Y-%m-%d_%H-%M","lifetime_value":2,"lifetime_unit":"WEEK","allow_empty":true,"enabled":true,"schedule":{"minute":"30","hour":"5","dom":"*","month":"*","dow":"1","begin":"05:30","end":"05:59"}}' | json_field 'v["id"]')
        echo "snapshot task games/club/images: id $IMG"
    fi
    R=$(repl_id "hybrid images to hdd")
    if [ -z "$R" ]; then
        R=$(midclt call replication.create "{\"name\":\"hybrid images to hdd\",\"direction\":\"PUSH\",\"transport\":\"LOCAL\",\"source_datasets\":[\"games/club/images\"],\"target_dataset\":\"hdd/backup/images/club-images\",\"recursive\":false,\"periodic_snapshot_tasks\":[$IMG],\"auto\":true,\"retention_policy\":\"CUSTOM\",\"lifetime_value\":3,\"lifetime_unit\":\"WEEK\",\"hold_pending_snapshots\":true,\"readonly\":\"SET\",\"properties\":true,\"properties_override\":{\"volmode\":\"none\"},\"allow_from_scratch\":false,\"encryption\":false,\"large_block\":true,\"compressed\":true,\"retries\":5,\"enabled\":true}" | json_field 'v["id"]')
        echo "replication hybrid images to hdd: id $R"
    fi
else
    echo "no games/club/images (hybrid images volume): its backup tasks come with a run after it is created"
fi

# Club server VM disk (system, PostgreSQL, settings): daily, crash-consistent. A box set up by the old version of this
# script has the task «club-server VM to hdd» (target on hdd): it is kept, not duplicated - retarget it by hand.
VM=$(task_id "$VMZVOL")
if [ -z "$VM" ]; then
    VM=$(midclt call pool.snapshottask.create "{\"dataset\":\"$VMZVOL\",\"recursive\":false,\"naming_schema\":\"bkp-%Y-%m-%d_%H-%M\",\"lifetime_value\":7,\"lifetime_unit\":\"DAY\",\"allow_empty\":true,\"enabled\":true,\"schedule\":{\"minute\":\"30\",\"hour\":\"4\",\"dom\":\"*\",\"month\":\"*\",\"dow\":\"*\",\"begin\":\"04:30\",\"end\":\"04:59\"}}" | json_field 'v["id"]')
    echo "snapshot task $VMZVOL: id $VM"
fi
OLD=$(repl_id "club-server VM to hdd")
R=$(repl_id "club-server VM to backup")
if [ -n "$OLD" ]; then
    echo "replication club-server VM to hdd (id $OLD) exists: «club-server VM to backup» not created; move it with" >&2
    echo "  midclt call replication.update $OLD '{\"name\":\"club-server VM to backup\",\"target_dataset\":\"backup/vm/clubserver\"}'" >&2
elif [ -z "$R" ]; then
    R=$(midclt call replication.create "{\"name\":\"club-server VM to backup\",\"direction\":\"PUSH\",\"transport\":\"LOCAL\",\"source_datasets\":[\"$VMZVOL\"],\"target_dataset\":\"backup/vm/clubserver\",\"recursive\":false,\"periodic_snapshot_tasks\":[$VM],\"auto\":true,\"retention_policy\":\"CUSTOM\",\"lifetime_value\":14,\"lifetime_unit\":\"DAY\",\"lifetimes\":[{\"schedule\":{\"minute\":\"*\",\"hour\":\"*\",\"dom\":\"*\",\"month\":\"*\",\"dow\":\"7\"},\"lifetime_value\":8,\"lifetime_unit\":\"WEEK\"}],\"hold_pending_snapshots\":true,\"readonly\":\"SET\",\"properties\":true,\"properties_exclude\":[\"refreservation\"],\"properties_override\":{\"volmode\":\"none\"},\"allow_from_scratch\":false,\"encryption\":false,\"retries\":5,\"enabled\":true}" | json_field 'v["id"]')
    echo "replication club-server VM to backup: id $R"
fi

# Diskless golden: the img-* versions the club server publishes (it never deletes them itself), by name, daily.
if exists ssd/club/diskless/win11; then
    R=$(repl_id "diskless golden to backup")
    if [ -z "$R" ]; then
        R=$(midclt call replication.create '{"name":"diskless golden to backup","direction":"PUSH","transport":"LOCAL","source_datasets":["ssd/club/diskless/win11"],"target_dataset":"backup/diskless/win11","recursive":false,"name_regex":"img-.*","auto":true,"schedule":{"minute":"15","hour":"5","dom":"*","month":"*","dow":"*"},"retention_policy":"SOURCE","readonly":"SET","properties":true,"properties_override":{"volmode":"none"},"enabled":true}' | json_field 'v["id"]')
        echo "replication diskless golden to backup: id $R"
    fi
fi

echo "--- tasks"
midclt call pool.snapshottask.query '[]' | json_field '"\n".join("%s %s keep %s %s, cron min %s hour %s dow %s" % (t["id"], t["dataset"], t["lifetime_value"], t["lifetime_unit"], t["schedule"]["minute"], t["schedule"]["hour"], t["schedule"]["dow"]) for t in v)'
midclt call replication.query '[]' | json_field '"\n".join("%s %s: %s -> %s" % (r["id"], r["name"], r["source_datasets"], r["target_dataset"]) for r in v)'
zfs list -o name,quota,used,avail -r hdd backup

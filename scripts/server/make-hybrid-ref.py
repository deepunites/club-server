#!/usr/bin/env python3
# Builds the REFERENCE WIM of the hybrid image from a published diskless version (docs/imaging.md, "Эталонный WIM гибрида"):
# Windows exactly as the diskless golden img-<version> left it (NOT generalized) plus the offline registry fixes for
# booting from a local SSD. It is imported in the panel like any WIM and deployed only to the reference PC (PC-11) by a
# reimage job that explicitly allows a non-generalized image. Run on the club server VM as root, inside tmux (the
# capture takes a while):
#   sudo python3 make-hybrid-ref.py v2-2026-10-22 [--label ref-v2-2026-10-22]
#   sudo python3 make-hybrid-ref.py --cleanup [v2-2026-10-22]   (only remove what an interrupted run left behind;
#                                     with no version or --label every incoming/*.wim.partial goes too)
# Steps; every run first removes leftovers of an earlier one (found by name), and the same cleanup runs on any failure,
# Ctrl+C, SIGTERM or a dropped ssh session:
#   1. TrueNAS (JSON-RPC over wss with the server's API key from club-server.env, never printed): writable clone
#      <image zvol parent>/hybrid-ref-tmp of <Diskless__ImageZvol>@img-<version>, an initiator group with only this
#      VM's IQN, a writable extent, a target without CHAP, LUN 0, then a SendTargets check from here;
#   2. iscsiadm login (a node record for this one target only), the disk set read-only, the Windows NTFS partition
#      found by a read-only ntfs-3g probe; its SYSTEM hive is checked already here (clean, the services of step 5);
#   3. the system-compressed files (WOF, compact /CompactOS: reparse tag 0x80000017, provider FILE) made plain files
#      on the clone. The NTFS-3G capture would store them raw (reparse point + WofCompressedData stream, no data), and
#      DISM cannot apply that ("Error: 1 Incorrect function" at 1 %, PC-11 2026-10-23). The disk is writable for this
#      step only: an ntfs-3g read-write mount, every entry's reparse point read; each WofCompressedData stream
#      decompressed with libwim (ctypes: WOF's XPRESS/LZX are the WIM codecs), written as the file's data, the reparse
#      point and the stream removed, attributes and times put back, the security descriptor untouched. Then the disk is
#      read-only again and a fresh read-only mount is checked (no WOF left, sizes, sha256). A WIMBoot pointer (provider
#      WIM) or any unexpected WOF data fails the run before the first change;
#   4. wimlib-imagex capture straight from the partition (NTFS-3G mode: ACLs, named streams, reparse points, short
#      names) into <Imaging__Root>/incoming/<label>.wim.partial, checked for WOF reparse tags and streams
#      (wimlib-imagex dir --detailed); logout, everything created in TrueNAS removed (if that fails, only a warning:
#      the WIM is finished and the removal tried again at the end);
#   5. the SYSTEM hive extracted from the WIM and edited with hivex in the Select\Current control set: storahci and
#      stornvme boot-start without StartOverride; the NIC/iSCSI services the diskless preparation made boot-start
#      (Start=0, BootFlags=1: install.cmd of extract-winsetup.sh, prepare-golden.ps1) back to Start=3 without
#      BootFlags; MSiSCSI automatic. Put back with wimlib-imagex update, together with the removal of the helper's
#      per-machine state files (credentials.bin*, assignment.json*) should any be there;
#   6. wimlib-imagex verify, rename to <label>.wim (the panel lists only top-level *.wim), size and sha256.
# A failure deletes the .partial, except a verified one whose final name was taken meanwhile.
# Offline check of the WOF decoder on one sample (no root, no TrueNAS; unit tests: scripts/server/test-wof.py):
#   python3 make-hybrid-ref.py --selftest-wof <reparse data: hex or a file> <WofCompressedData file> <size> [--out <file>]
# Needs: wimtools (with its libwim15t64), ntfs-3g, open-iscsi, python3-websocket, python3-hivex.
import argparse
import collections
import contextlib
import ctypes
import ctypes.util
import errno
import fcntl
import glob
import hashlib
import http.client
import json
import os
import re
import shutil
import signal
import ssl
import stat
import struct
import subprocess
import sys
import tempfile
import time
import traceback
import uuid
from types import SimpleNamespace

try:
    import websocket  # python3-websocket
except ImportError:
    websocket = None
try:
    import hivex  # python3-hivex
except ImportError:
    hivex = None

ENV_FILE = "/etc/club-server/club-server.env"
INITIATOR_FILE = "/etc/iscsi/initiatorname.iscsi"
LOCK_FILE = "/run/make-hybrid-ref.lock"
LOG_FILE = "/var/log/make-hybrid-ref.log"
TMP = "hybrid-ref-tmp"                             # clone (next to the image zvol), extent and target
GROUP_COMMENT = "clubsrv hybrid-ref-tmp"           # initiator group
SNAPSHOT_PREFIX = "img-"                           # DisklessOptions.SnapshotPrefix
LABEL = re.compile(r"^[a-z0-9][a-z0-9._-]{0,39}$")  # ImageLibrary.LabelPattern
API_VERSIONS = ["v25.10.0", "v25.10.1", "v25.10.2", "v25.10.3", "v25.10.4", "v25.10.5"]  # TrueNasOptions
CALL_TIMEOUT = 60                                  # TrueNasOptions.CallTimeoutSec
EXTENT_ATTEMPTS, EXTENT_RETRY_SEC = 5, 2           # LibraryOptions.ExtentAttempts / ExtentRetryDelayMs
GIB = 1 << 30
WIM_ENV = dict(os.environ, WIMLIB_IMAGEX_IGNORE_CASE="1")  # paths in the WIM matched as Windows matches them
SYSTEM_HIVE = "/Windows/System32/config/SYSTEM"
STATE_DIR = "/ProgramData/ClubDiskless"
STATE_FILES = ("credentials.bin", "assignment.json")  # seal-golden.ps1 $StateFiles (and their .tmp)
# DISM's default exclusions (also wimlib's default capture config) plus the helper's per-machine state; the state files
# are looked for in the WIM afterwards as well.
EXCLUDE = ("\\$ntfs.log", "\\hiberfil.sys", "\\pagefile.sys", "\\swapfile.sys", "\\System Volume Information",
           "\\RECYCLER", "\\$RECYCLE.BIN", "\\$Recycle.Bin", "\\Windows\\CSC",
           "\\ProgramData\\ClubDiskless\\credentials.bin*", "\\ProgramData\\ClubDiskless\\assignment.json*")
REG_SZ, REG_EXPAND_SZ, REG_DWORD = 1, 2, 4
NET_CLASS = "{4d36e972-e325-11ce-bfc1-08002be10318}"
STORAGE_SERVICES = ("storahci", "stornvme")
# Made boot-start for the iSCSI system disk: by install.cmd (extract-winsetup.sh, only those that exist) and by
# prepare-golden.ps1 (iScsiPrt and the boot NIC's driver, whatever its name: the drivers of all PCI network cards the
# image has seen are added at run time).
BOOT_START_SERVICES = ("iScsiPrt", "rt640x64", "rtcx21x64", "e1dexpress", "e1iexpress", "e2fexpress")
# WOF "system compression", provider FILE (ntfs-3g-system-compression, wimlib include/wimlib/wof.h, Linux fs/ntfs3):
# reparse data {tag, length 16, 0} {WOF version 1, provider} {provider version 1, format}; the unnamed $DATA is sparse
# with the real size; WofCompressedData = a table of chunk count - 1 offsets (u32, u64 above 4 GiB - 1) counted from the
# end of the table, then the chunks, each compressed on its own, stored as is when that would not make it smaller.
WOF_TAG = 0x80000017                               # IO_REPARSE_TAG_WOF
WOF_PROVIDER_WIM, WOF_PROVIDER_FILE = 1, 2         # WIM = WIMBoot pointer files: no data of their own in the volume
WOF_FORMATS = {0: ("XPRESS4K", 4096), 1: ("LZX", 32768), 2: ("XPRESS8K", 8192), 3: ("XPRESS16K", 16384)}  # chunk size
WOF_STREAM = "WofCompressedData"
REPARSE_XATTR = "system.ntfs_reparse_data"         # ntfs-3g: the whole $REPARSE_POINT attribute, header included
FILE_ATTR_NORMAL, FILE_ATTR_SPARSE, FILE_ATTR_REPARSE = 0x80, 0x200, 0x400
FILE_ATTR_SETTABLE = 0x1 | 0x2 | 0x4 | 0x20 | 0x100 | 0x1000 | 0x2000  # R H S A T O NCI: what system.ntfs_attrib sets
# ntfs-3g read-write mount of the clone: norecover (an unclean NTFS journal fails instead of being reset; a hibernated
# Windows is refused read-write, see ntfs_mounted), noatime (reading must not change the access times the WIM keeps),
# streams_interface=windows (file:stream paths; the xattr interface refuses the streams of a reparse point). No FUSE
# timeouts: ntfs-3g hands options it does not know only to fuse_mount(), which drops attr_timeout/entry_timeout; its
# own are attr_timeout=0 read-write, entry_timeout the default 1 s (the stale inode this leaves: lstat_new_type).
NTFS_RW = "rw,norecover,noatime,nodev,nosuid,noexec,streams_interface=windows"
NTFS_CHECK = "ro,nodev,nosuid,noexec,streams_interface=windows"
WOF_LOOKUP_PAUSE = 2.5                             # s between the two WOF passes: > entry_timeout, fresh lookups
EIO_TRIES, EIO_PAUSE = 20, 0.25                    # EIO from a stale FUSE inode: up to 5 s per call


class Failure(Exception):
    """An expected failure; the message says what is wrong."""


class Interrupted(Exception):
    pass


def on_signal(signum, _frame):
    raise Interrupted(f"stopped by {signal.Signals(signum).name}")


def log(text=""):
    try:
        print(text, flush=True)
    except OSError:
        pass  # the terminal is gone (ssh dropped): the run goes on, the log file has every line
    try:
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {text}\n")
    except OSError:
        pass


def step(text):
    log()
    log(f"=== {text}")


def run(cmd, check=True, capture=True, stdin=None, env=None):
    """Runs a command. capture: the output is returned (and shown on failure); otherwise it goes to the terminal."""
    proc = subprocess.Popen(cmd, text=True, env=env,
                            stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL,
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.STDOUT if capture else None)
    try:
        out, _ = proc.communicate(stdin)
    except BaseException:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(30)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        raise
    if check and proc.returncode != 0:
        tail = (out or "").strip()[-1500:]
        raise Failure(f"{' '.join(cmd)} exited with {proc.returncode}" + (f":\n{tail}" if tail else ""))
    return proc.returncode, out or ""


def read_env(path):
    env = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            value = value.strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
                value = value[1:-1]
            env[key.strip()] = value
    return env


# ---- TrueNAS --------------------------------------------------------------------------------------------------------

class RpcError(Exception):
    """JSON-RPC error. Only the code, reason and validation messages are shown: data.trace may carry call arguments."""

    def __init__(self, method, error):
        data = error.get("data") if isinstance(error.get("data"), dict) else {}
        extra = "; ".join(f"{item[0]}: {item[1]}" for item in data.get("extra") or [] if isinstance(item, list) and len(item) >= 2)
        super().__init__(f"{method}: {error.get('code')} {data.get('reason') or error.get('message')}" + (f" ({extra})" if extra else ""))


class TrueNas:
    """
    JSON-RPC 2.0 over wss as Club.TrueNas does it (TrueNasClient, TrueNasConnection): the highest whitelisted API version
    from /api/versions, auth.login_ex with API_KEY_PLAIN, TrueNAS 25.10+ only, TLS checked against the club CA. Connects
    on first use; closed during the capture (nginx drops /api after 60 s of silence) and opened again for the cleanup.
    """

    def __init__(self, env):
        self.host = env.get("TrueNas__Host", "")
        self.port = int(env.get("TrueNas__Port") or 443)
        self.username = env.get("TrueNas__Username", "")
        self.ca = env.get("TrueNas__CaCertificatePath", "")
        self._api_key = env.get("TrueNas__ApiKey", "")
        self.allowed = [v for k, v in env.items() if k.startswith("TrueNas__AllowedApiVersions__")] or API_VERSIONS
        missing = [k for k, v in (("TrueNas__Host", self.host), ("TrueNas__Username", self.username),
                                  ("TrueNas__ApiKey", self._api_key), ("TrueNas__CaCertificatePath", self.ca)) if not v]
        if missing:
            raise Failure(f"not set in {ENV_FILE}: {', '.join(missing)}")
        self.ws = None

    def call(self, method, *params):
        if self.ws is None:
            self._connect()
        return self._rpc(method, list(params))

    def close(self):
        if self.ws is not None:
            try:
                self.ws.close()
            except Exception:
                pass
            self.ws = None

    def _rpc(self, method, params):
        request_id = uuid.uuid4().hex
        self.ws.send(json.dumps({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}))
        while True:
            message = json.loads(self.ws.recv())
            if message.get("id") != request_id:
                continue  # notifications
            if message.get("error"):
                raise RpcError(method, message["error"])
            return message.get("result")

    def _connect(self):
        context = ssl.create_default_context(cafile=self.ca)
        https = http.client.HTTPSConnection(self.host, self.port, context=context, timeout=15)
        try:
            https.request("GET", "/api/versions")
            response = https.getresponse()
            available = json.loads(response.read()) if response.status == 200 else []
        finally:
            https.close()
        chosen = max((v for v in available if v in self.allowed), key=lambda v: tuple(int(p) for p in re.findall(r"\d+", v)), default=None)
        if chosen is None:
            raise Failure(f"no allowed TrueNAS API version: server offers {available}, allowed {self.allowed}")
        self.ws = websocket.create_connection(f"wss://{self.host}:{self.port}/api/{chosen}", timeout=CALL_TIMEOUT,
                                              sslopt={"ca_certs": self.ca, "cert_reqs": ssl.CERT_REQUIRED, "check_hostname": True})
        try:
            # A wrong key does not raise: response_type tells (EXPIRED also when the key was revoked).
            result = self._rpc("auth.login_ex", [{"mechanism": "API_KEY_PLAIN", "username": self.username,
                                                  "api_key": self._api_key, "login_options": {"user_info": False}}])
            response_type = result.get("response_type") if isinstance(result, dict) else None
            if response_type != "SUCCESS":
                raise Failure(f"TrueNAS login rejected: {response_type or 'unknown response'}")
            release = self._rpc("system.version_short", []) or ""
            parts = re.split(r"[.-]", release)
            if not (len(parts) >= 2 and parts[0].isdigit() and parts[1].isdigit() and (int(parts[0]), int(parts[1])) >= (25, 10)):
                raise Failure(f"TrueNAS {release} is not supported: 25.10 or newer is required")
        except BaseException:
            self.close()
            raise
        log(f"TrueNAS {release} at {self.host}, API {chosen}")


def raw(prop):
    """ZFS property as the middleware returns it ({value, rawvalue, parsed}); compared by rawvalue like TrueNasStorage."""
    return (prop.get("rawvalue") or prop.get("value")) if isinstance(prop, dict) else prop


def get_dataset(tn, dataset_id):  # TrueNasStorage.GetDatasetAsync
    rows = tn.call("pool.dataset.query", [["id", "=", dataset_id]], {"extra": {"retrieve_children": False}})
    return rows[0] if rows else None


def get_snapshot(tn, snapshot_id):  # TrueNasStorage.GetSnapshotAsync
    rows = tn.call("pool.snapshot.query", [["id", "=", snapshot_id]], {"extra": {"properties": ["clones"]}})
    return rows[0] if rows else None


def get_one(tn, method, field, value):  # GetExtentAsync, GetTargetAsync, GetInitiatorGroupAsync
    rows = tn.call(method, [[field, "=", value]])
    return rows[0] if rows else None


def ensure(create, query, what):
    """Create, then decide by a fresh query, not by the error text (TrueNasStorage.Ensure*Async)."""
    failure = None
    try:
        create()
    except RpcError as error:
        failure = error
    found = query()
    if not found:
        raise Failure(f"{what} was not created" + (f": {failure}" if failure else ""))
    return found


def expose(tn, cfg):
    """Clone and iSCSI objects, in the server's order for a seat disk (SeatDisks.ExposeAsync), but without CHAP: the
    initiator group admits only this VM. Writable extent on purpose: on TrueNAS 25.10 a read-only device breaks the
    `scstadmin -force` reloads (TargetVerifier.cs); the clone is disposable, and this host writes to it only in the
    WOF step (decompress_wof)."""
    if get_snapshot(tn, cfg.snapshot) is None:
        rows = tn.call("pool.snapshot.query", [["id", "^", f"{cfg.image_zvol}@{SNAPSHOT_PREFIX}"]])
        known = ", ".join(sorted(r["id"].split("@", 1)[1][len(SNAPSHOT_PREFIX):] for r in rows)) or "none"
        raise Failure(f"no snapshot {cfg.snapshot}; published versions: {known}")

    log(f"clone {cfg.clone} of {cfg.snapshot}")
    clone = ensure(lambda: tn.call("pool.snapshot.clone", {"snapshot": cfg.snapshot, "dataset_dst": cfg.clone,
                                                           "dataset_properties": {"clubsrv:role": "hybrid-ref"}}),
                   lambda: get_dataset(tn, cfg.clone), f"clone {cfg.clone}")
    if raw(clone.get("origin")) != cfg.snapshot:
        raise Failure(f"{cfg.clone} exists but is not a clone of {cfg.snapshot} (origin '{raw(clone.get('origin'))}')")

    log(f"initiator group '{GROUP_COMMENT}': only {cfg.vm_iqn}")
    group = ensure(lambda: tn.call("iscsi.initiator.create", {"initiators": [cfg.vm_iqn], "comment": GROUP_COMMENT}),
                   lambda: get_one(tn, "iscsi.initiator.query", "comment", GROUP_COMMENT), "initiator group")

    log(f"extent {TMP} (zvol/{cfg.clone})")
    for attempt in range(1, EXTENT_ATTEMPTS + 1):
        try:
            extent = ensure(lambda: tn.call("iscsi.extent.create", {"name": TMP, "type": "DISK", "disk": "zvol/" + cfg.clone, "ro": False,
                                                                    "comment": "clubsrv hybrid reference capture (temporary)"}),
                            lambda: get_one(tn, "iscsi.extent.query", "name", TMP), f"extent {TMP}")
            break
        except Failure:
            # The /dev/zvol node appears after the clone asynchronously (udev); extent.create refuses without it.
            if attempt == EXTENT_ATTEMPTS:
                raise
            log(f"  not created yet (attempt {attempt}), retrying")
            time.sleep(EXTENT_RETRY_SEC)

    log(f"target {cfg.iqn}, LUN 0")
    target = ensure(lambda: tn.call("iscsi.target.create", {"name": TMP, "alias": TMP, "mode": "ISCSI", "groups": [
                        {"portal": cfg.portal_id, "initiator": group["id"], "authmethod": "NONE"}]}),
                    lambda: get_one(tn, "iscsi.target.query", "name", TMP), f"target {TMP}")
    ensure(lambda: tn.call("iscsi.targetextent.create", {"target": target["id"], "extent": extent["id"], "lunid": 0}),
           lambda: tn.call("iscsi.targetextent.query", [["target", "=", target["id"]], ["extent", "=", extent["id"]]]), "LUN 0")
    ensure_visible(tn, cfg, target["id"])


def ensure_visible(tn, cfg, target_id):
    """
    TargetVerifier.EnsureVisibleAsync: the middleware ignores the exit code of `scstadmin -force -config`, so a created
    target may stay disabled without its LUN (a read-only device in copy_manager_tgt, stand 2026-10-02). Checked with
    SendTargets from here, repaired by another reload (iscsi.target.update with no changes), at most read-only extents
    + 2 times. Discovery is nonpersistent: a normal one would rewrite this host's node records (the images volume).
    """
    read_only = sum(1 for e in tn.call("iscsi.extent.query") if e.get("ro") and e.get("enabled"))
    for reloads in range(read_only + 3):
        time.sleep(1)  # LibraryOptions.VerifyDelayMs
        rc, out = run(["iscsiadm", "-m", "discovery", "-t", "sendtargets", "-p", cfg.portal, "-o", "nonpersistent"], check=False)
        if rc == 0 and cfg.iqn in [line.split()[-1] for line in out.splitlines() if line.strip()]:
            if reloads:
                log(f"  visible after {reloads} extra reload(s)")
            return
        if reloads == read_only + 2:
            raise Failure(f"{cfg.iqn} is not visible at {cfg.portal} after {reloads} reload(s); discovery said:\n{out.strip()}")
        log(f"  not visible at {cfg.portal} yet; re-applying the iSCSI configuration")
        tn.call("iscsi.target.update", target_id, {})


def cleanup(tn, cfg):
    """
    Everything this script creates, found by name; safe to repeat. This host first (mounts, iSCSI session, node record:
    TrueNAS refuses to delete a target with a session), then LUN, target, extent, initiator group (only once no target
    refers to it: TrueNAS would drop the reference and open that target to everyone) and the clone. Never force.
    """
    disk = our_disk(cfg)
    if disk:
        with open("/proc/self/mounts", encoding="utf-8") as f:
            mounts = [line.split()[:2] for line in f]
        for source, target in mounts:
            if source == disk or re.fullmatch(re.escape(disk) + r"\d+", source):
                path = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), target)  # \040 = space
                log(f"unmount {path}")
                run(["umount", path])
    if cfg.iqn in run(["iscsiadm", "-m", "session"], check=False)[1].split():
        log(f"iSCSI logout {cfg.iqn}")
        run(["iscsiadm", "-m", "node", "-T", cfg.iqn, "--logout"])
    if cfg.iqn in run(["iscsiadm", "-m", "node"], check=False)[1].split():
        run(["iscsiadm", "-m", "node", "-T", cfg.iqn, "-o", "delete"])

    target = get_one(tn, "iscsi.target.query", "name", TMP)
    if target:
        for lun in tn.call("iscsi.targetextent.query", [["target", "=", target["id"]]]):
            log(f"delete LUN {lun['lunid']} of target {TMP}")
            tn.call("iscsi.targetextent.delete", lun["id"])
        log(f"delete target {TMP}")
        for attempt in range(1, 6):
            reason = None
            try:
                tn.call("iscsi.target.delete", target["id"], False, False)  # TrueNasStorage.DeleteTargetAsync
            except RpcError as error:
                reason = error
            if get_one(tn, "iscsi.target.query", "name", TMP) is None:
                break
            if attempt == 5:
                sessions = sorted({s.get("initiator", "?") for s in tn.call("iscsi.global.sessions") if s.get("target", "").endswith(":" + TMP)})
                raise Failure(f"target {TMP} not deleted ({reason or 'still present'}); sessions from: {', '.join(sessions) or 'none'}")
            time.sleep(3)  # the session of our logout may still be listed
    extent = get_one(tn, "iscsi.extent.query", "name", TMP)
    if extent:
        log(f"delete extent {TMP}")
        try:
            tn.call("iscsi.extent.delete", extent["id"], False, False)  # TrueNasStorage.DeleteExtentAsync
        except RpcError as error:
            if get_one(tn, "iscsi.extent.query", "name", TMP):
                raise Failure(f"extent {TMP} not deleted: {error}")
    group = get_one(tn, "iscsi.initiator.query", "comment", GROUP_COMMENT)
    if group:
        users = [t["name"] for t in tn.call("iscsi.target.query") if any(g.get("initiator") == group["id"] for g in t.get("groups") or [])]
        if users:
            raise Failure(f"initiator group '{GROUP_COMMENT}' is used by target(s) {', '.join(users)}: not deleted")
        log(f"delete initiator group '{GROUP_COMMENT}'")
        tn.call("iscsi.initiator.delete", group["id"])
    clone = get_dataset(tn, cfg.clone)
    if clone:
        origin = raw(clone.get("origin")) or ""
        if not origin.startswith(f"{cfg.image_zvol}@"):
            raise Failure(f"{cfg.clone} exists but is not a clone of {cfg.image_zvol} (origin '{origin}'): not deleting it")
        attachments = tn.call("pool.dataset.attachments", cfg.clone)
        if attachments:
            raise Failure(f"{cfg.clone} still has attachments: {json.dumps(attachments)[:300]}")
        log(f"destroy clone {cfg.clone}")
        # recursive: snapshots a periodic task may have taken of the clone go with it (seat clones, SeatDisks.TearDownAsync)
        try:
            tn.call("pool.dataset.delete", cfg.clone, {"recursive": True, "force": False})
        except RpcError as error:
            if get_dataset(tn, cfg.clone):
                raise Failure(f"{cfg.clone} not deleted: {error}")


# ---- this host: iSCSI disk and the Windows partition ----------------------------------------------------------------

def our_disk(cfg):
    links = glob.glob(f"/dev/disk/by-path/ip-*-iscsi-{glob.escape(cfg.iqn)}-lun-0")
    return os.path.realpath(links[0]) if links else None


def login(cfg):
    node = ["iscsiadm", "-m", "node", "-T", cfg.iqn, "-p", cfg.portal]
    run(node + ["-o", "new"])
    run(node + ["-o", "update", "-n", "node.startup", "-v", "manual"])
    run(node + ["-o", "update", "-n", "node.session.auth.authmethod", "-v", "None"])
    log(f"iSCSI login {cfg.iqn} at {cfg.portal}")
    run(node + ["--login"])
    deadline = time.monotonic() + 60
    while (disk := our_disk(cfg)) is None:
        if time.monotonic() > deadline:
            raise Failure(f"no block device for {cfg.iqn} after login (/dev/disk/by-path/…-iscsi-{cfg.iqn}-lun-0)")
        run(["udevadm", "settle"], check=False)
        time.sleep(1)
    # Partitions follow the disk's flag. Nothing on this host writes to the clone, except decompress_wof, which lifts
    # the flag for its own read-write mount only.
    run(["blockdev", "--setro", disk])
    size = int(run(["blockdev", "--getsize64", disk])[1].strip())
    log(f"disk {disk}: {size / GIB:.1f} GiB, read-only")
    return disk


def find_ci(root, path):
    """The file at root/path matched case-insensitively, as Windows matches it, or None."""
    current = root
    for part in path.strip("/").split("/"):
        try:
            names = os.listdir(current)
        except OSError:
            return None
        match = next((n for n in names if n.lower() == part.lower()), None)
        if match is None:
            return None
        current = os.path.join(current, match)
    return current if os.path.isfile(current) else None


def find_windows(disk, workdir):
    """
    The NTFS partition with Windows/System32/config/SYSTEM (a read-only ntfs-3g probe); returns it and its used bytes.
    Its SYSTEM hive is copied from the probe and checked now (check_hive): what would make the registry fixes fail
    must fail before the capture has loaded the pool the seats run on.
    """
    run(["udevadm", "settle"], check=False)
    tree = json.loads(run(["lsblk", "-J", "-b", "-p", "-o", "NAME,TYPE,SIZE", disk])[1])
    probe = os.path.join(workdir, "probe")
    os.makedirs(probe)
    found = []
    for part in [p for p in tree["blockdevices"][0].get("children") or [] if p.get("type") == "part"]:
        name, size = part["name"], int(part["size"])
        fstype = run(["blkid", "-p", "-o", "value", "-s", "TYPE", name], check=False)[1].strip()
        hive, used = None, 0
        if fstype == "ntfs":
            run(["ntfs-3g", "-o", "ro", name, probe])
            try:
                if source := find_ci(probe, SYSTEM_HIVE):
                    hive = os.path.join(workdir, "SYSTEM." + os.path.basename(name))
                    shutil.copyfile(source, hive)
                st = os.statvfs(probe)
                used = (st.f_blocks - st.f_bfree) * st.f_frsize
            finally:
                run(["umount", probe])
        log(f"  {name}  {size / GIB:7.1f} GiB  {fstype or '-'}" + (f"  Windows, {used / GIB:.1f} GiB used" if hive else ""))
        if hive:
            found.append((size, name, used, hive))
    if not found:
        raise Failure(f"no NTFS partition with {SYSTEM_HIVE} on {disk}")
    if len(found) > 1:
        log(f"WARNING: {len(found)} partitions with Windows; taking the largest")
    _, name, used, hive = max(found)
    check_hive(hive)
    return name, used


# ---- system-compressed (WOF) files ----------------------------------------------------------------------------------

class Libwim:
    """
    libwim.so.15 (wimtools' library) through ctypes. WOF's XPRESS and LZX are the WIM formats (ntfs-3g-system-compression
    and Linux ntfs3 decompress them with wimlib's code). One decompressor per WOF format, made for exactly its chunk
    size: LZX's window (32 KiB) decides the number of main-tree symbols.
    """
    XPRESS, LZX = 1, 2  # enum wimlib_compression_type

    def __init__(self):
        self.lib = None
        for name in ("libwim.so.15", ctypes.util.find_library("wim")):
            try:
                self.lib = ctypes.CDLL(name) if name else None
            except OSError:
                continue
            if self.lib:
                break
        if self.lib is None:
            raise Failure("libwim.so.15 (sudo apt install libwim15t64; wimtools depends on it)")
        self.lib.wimlib_create_decompressor.argtypes = [ctypes.c_int, ctypes.c_size_t, ctypes.POINTER(ctypes.c_void_p)]
        self.lib.wimlib_create_decompressor.restype = ctypes.c_int
        self.lib.wimlib_decompress.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p]
        self.lib.wimlib_decompress.restype = ctypes.c_int
        self._decompressors = {}
        self._out = ctypes.create_string_buffer(max(chunk for _, chunk in WOF_FORMATS.values()))

    def decompress(self, fmt, data, size):
        """One chunk of WOF format fmt; exactly size bytes or Failure."""
        name, chunk = WOF_FORMATS[fmt]
        handle = self._decompressors.get(fmt)
        if handle is None:
            handle = ctypes.c_void_p()
            rc = self.lib.wimlib_create_decompressor(self.LZX if name == "LZX" else self.XPRESS, chunk, ctypes.byref(handle))
            if rc:
                raise Failure(f"wimlib_create_decompressor({name}, {chunk}) returned {rc}")
            self._decompressors[fmt] = handle
        if self.lib.wimlib_decompress(data, len(data), self._out, size, handle):
            raise Failure(f"{len(data)} bytes of {name} data do not decompress to {size} bytes")
        return ctypes.string_at(self._out, size)


def parse_wof_reparse(data):
    """The format of a WOF reparse point (24 bytes: the whole attribute, as system.ntfs_reparse_data returns it)."""
    if len(data) < 16 or struct.unpack_from("<I", data)[0] != WOF_TAG:
        raise Failure(f"not a WOF reparse point: {data[:24].hex()}")
    length, version, provider = struct.unpack_from("<H2xII", data, 4)
    if provider == WOF_PROVIDER_WIM:
        raise Failure("WOF provider WIM: a WIMBoot pointer file, its data is in a WIM outside the volume")
    if version != 1 or provider != WOF_PROVIDER_FILE or length != 16 or len(data) != 24:
        raise Failure(f"unknown WOF reparse data (version {version}, provider {provider}, {len(data)} bytes): {data.hex()}")
    file_version, fmt = struct.unpack_from("<II", data, 16)
    if file_version != 1 or fmt not in WOF_FORMATS:
        raise Failure(f"unknown WOF FILE provider data (version {file_version}, format {fmt})")
    return fmt


def wof_layout(size, fmt):
    """(chunk size, chunk count, offset width, table length) of the WofCompressedData of a size-byte file."""
    chunk = WOF_FORMATS[fmt][1]
    count = -(-size // chunk)
    width = 4 if size <= 0xFFFFFFFF else 8
    return chunk, count, width, max(count - 1, 0) * width


def wof_chunks(size, fmt, csize, table):
    """[(stored size, size)] of every chunk, from the offsets table; Failure if they do not fit csize exactly."""
    chunk, count, width, table_len = wof_layout(size, fmt)
    if csize < table_len or len(table) != table_len:
        raise Failure(f"{WOF_STREAM} has {csize} bytes, less than its table of {count - 1} offsets for {size} bytes")
    if not count:
        if csize:
            raise Failure(f"{WOF_STREAM} has {csize} bytes for an empty file")
        return []
    starts = [0, *struct.unpack(f"<{count - 1}{'I' if width == 4 else 'Q'}", table)]
    ends = starts[1:] + [csize - table_len]
    chunks = []
    for i, (start, end) in enumerate(zip(starts, ends)):
        expected = chunk if i < count - 1 else size - chunk * (count - 1)
        if not 0 < end - start <= expected:  # also catches offsets that do not increase or run past the stream
            raise Failure(f"{WOF_STREAM} chunk {i} of {count}: stored size {end - start} for {expected} bytes")
        chunks.append((end - start, expected))
    return chunks


def read_exact(f, length):
    data = f.read(length)
    if len(data) != length:
        raise Failure(f"{WOF_STREAM} ends early ({len(data)} of {length} bytes read)")
    return data


def wof_decode(src, size, fmt, csize, out, decompress):
    """
    The data of a WOF file: its WofCompressedData read from src (from the start, csize bytes), written to out.
    decompress(fmt, data, size) does one chunk. Returns (sha256 hex, chunk count, chunks stored as they are).
    """
    table_len = wof_layout(size, fmt)[3]
    chunks = wof_chunks(size, fmt, csize, read_exact(src, table_len))
    digest, stored_raw = hashlib.sha256(), 0
    for i, (stored, expected) in enumerate(chunks):
        data = read_exact(src, stored)
        if stored == expected:
            stored_raw += 1  # incompressible: kept as it is
        else:
            try:
                data = decompress(fmt, data, expected)
            except Failure as error:
                raise Failure(f"chunk {i} of {len(chunks)}: {error}") from None
            if len(data) != expected:
                raise Failure(f"chunk {i} of {len(chunks)}: {len(data)} bytes instead of {expected}")
        digest.update(data)
        out.write(data)
    if src.read(1):
        raise Failure(f"{WOF_STREAM} is longer than its {csize} bytes")
    return digest.hexdigest(), len(chunks), stored_raw


def xattr(path, name):
    return os.getxattr(path, name, follow_symlinks=False)  # ntfs-3g shows a WOF file as a symlink: never followed


def ntfs_attrib(path):
    return struct.unpack("=I", xattr(path, "system.ntfs_attrib"))[0]  # CPU byte order


def wof_scan(root):
    """
    Every inode with a WOF reparse point under root (an ntfs-3g mount): {inode: entry (path relative to root, format,
    WofCompressedData size, the paths of its other hard links)}, the problems found and {other reparse tag: count}.
    ntfs-3g without the system-compression plugin (none is packaged) shows WOF files as broken symlinks, with it as
    regular files: the reparse data is read for every entry that is not a directory. Directories that are reparse
    points (junctions) show as symlinks and are not entered.
    """
    found, problems, others, entries, seen_dirs = {}, [], collections.Counter(), 0, set()
    progress = time.monotonic() + 30
    pending = [""]
    while pending:
        directory = pending.pop()
        try:
            names = sorted(os.listdir(os.path.join(root, directory)))
        except OSError as error:
            raise Failure(f"cannot list /{directory}: {error}") from None
        for name in names:
            rel = f"{directory}/{name}" if directory else name
            path = os.path.join(root, rel)
            entries += 1
            try:
                st = os.lstat(path)
                if stat.S_ISDIR(st.st_mode):
                    if st.st_ino not in seen_dirs:
                        seen_dirs.add(st.st_ino)
                        pending.append(rel)
                    continue
                try:
                    data = xattr(path, REPARSE_XATTR)
                except OSError as error:
                    if error.errno != errno.ENODATA:
                        raise
                    continue  # not a reparse point
                tag = struct.unpack_from("<I", data)[0] if len(data) >= 4 else None
                if tag != WOF_TAG:
                    others[tag] += 1
                elif st.st_ino in found:
                    found[st.st_ino].links.append(rel)
                else:
                    fmt = parse_wof_reparse(data)
                    csize = os.lstat(path + ":" + WOF_STREAM).st_size
                    found[st.st_ino] = SimpleNamespace(path=rel, fmt=fmt, csize=csize, links=[])
            except Failure as error:
                problems.append(f"/{rel}: {error}")
            except OSError as error:
                # ntfs-3g answers EIO for some reparse points it cannot present (2026-10-24 on img-v2: the App
                # Execution Alias ...\WindowsApps\ActionsMcpHost.exe, tag 0x8000001b). Such an entry cannot be read
                # here and is left as it is; should it be a WOF file after all, the check of the captured WIM
                # (no 0x80000017 tag) still fails the run.
                if error.errno != errno.EIO:
                    raise Failure(f"/{rel}: {error}") from None
                others["unreadable"] += 1
                log(f"  unreadable through ntfs-3g (EIO), left as it is: /{rel}")
            if time.monotonic() > progress:
                progress = time.monotonic() + 30
                log(f"  … {entries} entries, {len(found)} WOF files so far")
    for entry in found.values():  # the first hard link by name, not by the order of the walk
        entry.path, *entry.links = sorted([entry.path, *entry.links])
    return found, problems, others, entries


def lstat_new_type(path):
    """
    lstat after the reparse point is gone. The kernel still has the FUSE inode as the symlink it was (the dentry is
    trusted for ntfs-3g's entry_timeout of 1 s, which -o cannot change): the first getattr sees the other type, marks
    that inode bad and fails with EIO (fs/fuse, inode_wrong_type); the next lookup makes a new one (kernel 7.0, dev box
    2026-10-23).
    """
    st = None
    for attempt in range(EIO_TRIES):
        try:
            st = os.lstat(path)
        except OSError as error:
            if error.errno != errno.EIO or attempt == EIO_TRIES - 1:
                raise
            time.sleep(EIO_PAUSE)
            continue
        if stat.S_ISREG(st.st_mode):
            return st
        time.sleep(EIO_PAUSE)
    raise Failure(f"not a regular file after its reparse point was removed (mode {oct(st.st_mode) if st else '?'})")


def eio_retry(what, call):
    """call() again while ntfs-3g/FUSE answers EIO for an inode whose type changed (see lstat_new_type)."""
    for attempt in range(EIO_TRIES):
        try:
            return call()
        except OSError as error:
            if error.errno != errno.EIO or attempt == EIO_TRIES - 1:
                raise Failure(f"{what}: {error}") from None
            time.sleep(EIO_PAUSE)


def wof_unlink_reparse(root, entry):
    """
    First pass for one WOF inode: its attributes, times and security descriptor are kept for wof_convert_file, then its
    reparse point goes (ntfs-3g opens the streams of a reparse point only through a plugin). From here until the second
    pass the file is broken, acceptable only because the clone is destroyed on any failure.
    """
    path = os.path.join(root, entry.path)
    # system.ntfs_acl answers EIO for files of the real golden (club VM 2026-10-24), not for mkntfs test files. The
    # descriptor is only compared, never written (the conversion leaves the security id alone and wimlib reads the
    # descriptors through libntfs-3g), so a file whose descriptor cannot be read is converted without that comparison.
    try:
        acl = xattr(path, "system.ntfs_acl")
    except OSError as error:
        if error.errno != errno.EIO:
            raise
        acl = None
    meta = (eio_retry("read the attributes", lambda: ntfs_attrib(path)),
            eio_retry("read the times", lambda: xattr(path, "system.ntfs_times")), acl)
    eio_retry("remove the reparse point", lambda: os.removexattr(path, REPARSE_XATTR, follow_symlinks=False))
    return meta


def wof_convert_file(root, entry, codec):
    """
    Second pass for one WOF inode, after wof_unlink_reparse and the pause: made a plain file through the ntfs-3g
    read-write mount at root. The data overwrites the sparse unnamed stream (ntfs-3g allocates the holes and drops
    SPARSE once there are none); the times and attributes writing changes are put back; the security descriptor, the
    other streams, the short name and the object ID are not touched (the descriptor is compared).
    Returns (size, sha256, still sparse).
    """
    path = os.path.join(root, entry.path)
    stream = path + ":" + WOF_STREAM
    flags, times, acl = entry.meta
    size = lstat_new_type(path).st_size  # the unnamed stream's size: the size of the data
    # no O_TRUNC: the stream keeps its size, the writes fill it
    fd = eio_retry("open for writing", lambda: os.open(path, os.O_WRONLY | os.O_NOFOLLOW))
    try:
        src = eio_retry(f"open {WOF_STREAM}", lambda: open(stream, "rb", buffering=1 << 20))
    except BaseException:
        os.close(fd)  # an open file would keep the unmount of the failure path busy
        raise
    with open(fd, "wb", buffering=1 << 20) as out, src:
        digest, _, _ = wof_decode(src, size, entry.fmt, entry.csize, out, codec.decompress)
        out.flush()
        if os.fstat(fd).st_size != size:
            raise Failure(f"{os.fstat(fd).st_size} bytes after writing {size}")
    os.unlink(stream)
    now = ntfs_attrib(path)
    if (now ^ flags) & FILE_ATTR_SETTABLE:
        os.setxattr(path, "system.ntfs_attrib", struct.pack("=I", flags & FILE_ATTR_SETTABLE), follow_symlinks=False)
    # creation, last write, last access (CPU byte order); the 4th, the MFT change time, is always "now" (not in a WIM)
    os.setxattr(path, "system.ntfs_times", times[:24], follow_symlinks=False)
    now = ntfs_attrib(path)
    # NORMAL is no change: ntfs-3g reports it for a file with no attribute at all (libntfs-3g ntfs_get_ntfs_attrib),
    # e.g. one that had only REPARSE|SPARSE; wimlib's capture reads the attributes the same way, as Windows shows them.
    if now & FILE_ATTR_REPARSE or (now ^ flags) & ~(FILE_ATTR_SPARSE | FILE_ATTR_REPARSE | FILE_ATTR_NORMAL):
        raise Failure(f"attributes 0x{flags:x} became 0x{now:x}")
    if xattr(path, "system.ntfs_times")[:24] != times[:24]:
        raise Failure("the times were not put back")
    if acl is not None and xattr(path, "system.ntfs_acl") != acl:
        raise Failure("the security descriptor changed")
    return size, digest, bool(now & FILE_ATTR_SPARSE)


def mount_ntfs(device, mountpoint, options):
    run(["ntfs-3g", "-o", options, device, mountpoint])


def unmount(mountpoint):
    run(["umount", mountpoint])  # fuseblk: returns once ntfs-3g has written everything and closed the device


def mount_options(mountpoint):
    target = os.path.realpath(mountpoint)
    with open("/proc/self/mounts", encoding="utf-8") as f:
        for line in f:
            fields = line.split()
            if len(fields) >= 4 and re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), fields[1]) == target:
                return fields[3].split(",")
    raise Failure(f"{mountpoint} is not mounted")


@contextlib.contextmanager
def ntfs_mounted(device, mountpoint, options):
    """An ntfs-3g mount for the with-block; on a failure the unmount is only tried (cleanup() unmounts it again)."""
    os.makedirs(mountpoint, exist_ok=True)
    mount_ntfs(device, mountpoint, options)
    try:
        if options.split(",")[0] == "rw" and "rw" not in mount_options(mountpoint):
            raise Failure(f"ntfs-3g mounted {device} read-only: Windows hibernated (Fast Startup) or not shut down cleanly")
        yield mountpoint
    except BaseException:
        try:
            unmount(mountpoint)
        except Exception as error:
            log(f"WARNING: {error}")
        raise
    unmount(mountpoint)


def used_bytes(path):
    st = os.statvfs(path)
    return (st.f_blocks - st.f_bfree) * st.f_frsize


def wof_convert_volume(partition, mountpoint, codec):
    """The read-write part of decompress_wof: returns {inode: (path, size, sha256, other links)} and the bytes now used."""
    with ntfs_mounted(partition, mountpoint, NTFS_RW):
        used_before = used_bytes(mountpoint)
        log(f"reading the reparse data of every entry on {partition} …")
        files, problems, others, entries = wof_scan(mountpoint)
        formats = collections.Counter(WOF_FORMATS[e.fmt][0] for e in files.values())
        links = sum(len(e.links) for e in files.values())
        log(f"  {entries} entries; WOF: {len(files)} file(s)" + (f" (+{links} more hard links)" if links else "")
            + "".join(f", {name} {n}" for name, n in sorted(formats.items())))
        if others:
            log("  other reparse points, left as they are: " + ", ".join(
                f"0x{tag:08x} {n}" if isinstance(tag, int) else f"{tag or 'invalid'} {n}"
                for tag, n in sorted(others.items(), key=str)))
        if problems:
            raise Failure(f"{len(problems)} WOF file(s) this script cannot convert, nothing changed yet:\n  "
                          + "\n  ".join(problems[:20]) + ("\n  …" if len(problems) > 20 else ""))
        # Two passes (club VM 2026-10-24: removing the reparse point and opening the file right away gave EIO): first
        # every reparse point goes, then a pause longer than ntfs-3g's entry_timeout (1 s) so that the kernel looks the
        # files up again as the regular files they now are, then the data.
        ordered = sorted(files.items(), key=lambda item: item[1].path)
        log(f"  removing {len(files)} WOF reparse point(s) …")
        for ino, entry in ordered:
            try:
                entry.meta = wof_unlink_reparse(mountpoint, entry)
            except (Failure, OSError) as error:
                raise Failure(f"/{entry.path}: {error}") from None
        time.sleep(WOF_LOOKUP_PAUSE)
        converted, written, sparse, samples = {}, 0, 0, {}
        started, progress = time.monotonic(), time.monotonic() + 15
        for n, (ino, entry) in enumerate(ordered, 1):
            try:
                size, digest, still_sparse = wof_convert_file(mountpoint, entry, codec)
            except (Failure, OSError) as error:
                raise Failure(f"/{entry.path}: {error}") from None
            converted[ino] = (entry.path, size, digest, entry.links)
            written += size
            sparse += still_sparse
            if size:
                samples.setdefault(entry.fmt, (entry.path, digest))
            if time.monotonic() > progress or n == len(files):
                progress = time.monotonic() + 15
                log(f"  {n}/{len(files)} files, {written / GIB:.2f} GiB written ({time.monotonic() - started:.0f} s)")
        used = used_bytes(mountpoint)
    if files:
        log(f"  {sum(e.csize for e in files.values()) / GIB:.2f} GiB compressed → {written / GIB:.2f} GiB;"
            f" used on the volume {used_before / GIB:.1f} → {used / GIB:.1f} GiB")
        if sparse:
            log(f"  {sparse} file(s) still flagged sparse (all data written; DISM applies them as such)")
        log("  spot check, Get-FileHash on a seat of this version must print the same:")
        for _, (path, digest) in sorted(samples.items()):
            log(f"    {digest.upper()}  C:\\{path.replace('/', chr(92))}")
    return converted, used


def wof_verify_volume(partition, mountpoint, converted):
    """
    On a fresh read-only mount (nothing cached from the conversion, the disk read-only again): no WOF reparse point left,
    every converted inode a regular file of its size, all its hard links too, no WofCompressedData, the same sha256.
    """
    with ntfs_mounted(partition, mountpoint, NTFS_CHECK):
        log("check on a read-only mount …")
        files, problems, _, _ = wof_scan(mountpoint)
        if files or problems:
            left = [f"/{e.path}" for e in files.values()] + problems
            raise Failure(f"{len(left)} WOF file(s) left after the conversion, e.g. {', '.join(left[:5])}")
        progress = time.monotonic() + 15
        for n, (ino, (path, size, digest, links)) in enumerate(sorted(converted.items(), key=lambda item: item[1][0]), 1):
            full = os.path.join(mountpoint, path)
            for rel in (path, *links):
                st = os.lstat(os.path.join(mountpoint, rel))
                if not stat.S_ISREG(st.st_mode) or st.st_ino != ino or st.st_size != size:
                    raise Failure(f"/{rel}: mode {oct(st.st_mode)}, inode {st.st_ino}, {st.st_size} bytes; "
                                  f"expected a file, inode {ino}, {size} bytes")
            if ntfs_attrib(full) & FILE_ATTR_REPARSE:
                raise Failure(f"/{path}: still flagged as a reparse point")
            if os.path.lexists(full + ":" + WOF_STREAM):
                raise Failure(f"/{path}: {WOF_STREAM} is still there")
            if sha256(full) != digest:
                raise Failure(f"/{path}: the data read back is not the data written")
            if time.monotonic() > progress or n == len(converted):
                progress = time.monotonic() + 15
                log(f"  {n}/{len(converted)} files checked")
        return used_bytes(mountpoint)


def decompress_wof(disk, partition, workdir, codec):
    """
    Step 3: the WOF files of the clone made plain files; returns the bytes used on the partition afterwards. The disk is
    writable only for the read-write mount (the clone is disposable, the golden zvol and its snapshot are not touched):
    there is no other way to give these files their data. Any failure leaves a half-converted clone, which the usual
    cleanup destroys; the capture reads only a clone that passed the check.
    """
    mountpoint = os.path.join(workdir, "wof")
    run(["blockdev", "--setrw", disk])
    try:
        if run(["blockdev", "--getro", partition])[1].strip() != "0":
            raise Failure(f"{partition} is still read-only after blockdev --setrw {disk}")
        log(f"disk {disk}: writable for this step")
        converted, used = wof_convert_volume(partition, mountpoint, codec)
    finally:
        run(["blockdev", "--setro", disk], check=False)
    if run(["blockdev", "--getro", partition])[1].strip() != "1":
        raise Failure(f"{partition} is not read-only again (blockdev --setro {disk})")
    log(f"disk {disk}: read-only again")
    if not converted:
        log("no system-compressed files")
        return used
    return wof_verify_volume(partition, mountpoint, converted)


# Lines of `wimlib-imagex dir --detailed` (programs/imagex.c, print_dentry_detailed).
WIM_DIR_PATH = re.compile(r'^Full Path\s*= "(.*)"$')
WIM_DIR_TAG = re.compile(r"^Reparse Tag\s*= 0x([0-9a-fA-F]+)$")
WIM_DIR_STREAM = re.compile(r'^\s*Named data stream "(.*)":$')


def wim_wof_report(lines):
    """(entries, {reparse tag: count}, paths with the WOF tag, paths with a WofCompressedData stream)."""
    entries, tags, tagged, streams, path = 0, collections.Counter(), [], [], None
    for line in lines:
        line = line.rstrip("\n")
        if m := WIM_DIR_PATH.match(line):
            entries, path = entries + 1, m.group(1)
        elif m := WIM_DIR_TAG.match(line):
            tag = int(m.group(1), 16)
            tags[tag] += 1
            if tag == WOF_TAG:
                tagged.append(path)
        elif (m := WIM_DIR_STREAM.match(line)) and m.group(1).lower() == WOF_STREAM.lower():
            streams.append(path)
    return entries, tags, tagged, streams


def check_wim_no_wof(wim):
    """
    After the capture: no file of the WIM has the WOF reparse tag or a WofCompressedData stream. The output is long
    (every security descriptor in hex), so it is read as it comes. A Windows image always has other reparse points
    (junctions such as \\Documents and Settings): none at all means the output was not understood.
    """
    cmd = ["wimlib-imagex", "dir", wim, "1", "--detailed"]
    proc = subprocess.Popen(cmd, text=True, errors="replace", env=WIM_ENV, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    tail = collections.deque(maxlen=10)

    def lines():
        for line in proc.stdout:
            tail.append(line)
            yield line
    try:
        entries, tags, tagged, streams = wim_wof_report(lines())
        proc.wait()
    except BaseException:
        proc.kill()
        proc.wait()
        raise
    if proc.returncode != 0:
        raise Failure(f"{' '.join(cmd)} exited with {proc.returncode}:\n{''.join(tail).strip()}")
    if not entries or not tags:
        raise Failure(f"{' '.join(cmd)}: output not understood ({entries} entries, no reparse tags at all)")
    if tagged or streams:
        raise Failure(f"the WIM still has {len(tagged)} file(s) with reparse tag 0x{WOF_TAG:08x} and {len(streams)} with a "
                      f"{WOF_STREAM} stream, e.g. {', '.join((tagged + streams)[:5])}")
    log(f"  WIM: {entries} entries, reparse tags " + ", ".join(f"0x{t:08x} {n}" for t, n in sorted(tags.items()))
        + f"; no 0x{WOF_TAG:08x}, no {WOF_STREAM}")


def selftest_wof(reparse, stream, size, out=None, codec=None):
    """
    --selftest-wof: one WOF file decoded offline. reparse: the reparse data in hex (24 bytes, or the 16 after the
    header, as a WIM stores them) or a file with it, raw or in hex. Samples from an NTFS partition that has such files,
    read without mounting it: ntfscat -a 0xC0 <partition> <path> | xxd -p (reparse data),
    ntfscat -a 0x80 -n WofCompressedData <partition> <path> > stream, ntfscat <partition> <path> | wc -c (the size).
    """
    if os.path.isfile(reparse):
        with open(reparse, "rb") as f:
            data = f.read()
        try:
            data = bytes.fromhex(data.decode("ascii"))
        except ValueError:
            pass  # raw bytes
    else:
        data = bytes.fromhex(reparse)
    if len(data) == 16:
        data = struct.pack("<IHH", WOF_TAG, 16, 0) + data
    fmt = parse_wof_reparse(data)
    size = int(size)
    codec = codec or Libwim()
    with open(stream, "rb") as src, open(out or os.devnull, "wb") as dst:
        digest, chunks, stored_raw = wof_decode(src, size, fmt, os.fstat(src.fileno()).st_size, dst, codec.decompress)
    print(f"{WOF_FORMATS[fmt][0]}: {chunks} chunk(s), {stored_raw} stored uncompressed, {size} bytes"
          + (f" → {out}" if out else ""))
    print(f"sha256 {digest}")
    return 0


# ---- WIM ------------------------------------------------------------------------------------------------------------

def capture(cfg, partition, used, workdir):
    st = os.statvfs(cfg.incoming)
    free = st.f_bavail * st.f_frsize
    log(f"{partition}: {used / GIB:.1f} GiB used; free in {cfg.incoming}: {free / GIB:.1f} GiB")
    if free < used:
        raise Failure(f"not enough space in {cfg.incoming} (the WIM can be as large as the data on the partition)")
    config = os.path.join(workdir, "capture.ini")
    with open(config, "w", encoding="utf-8") as f:
        f.write("[ExclusionList]\n" + "".join(p + "\n" for p in EXCLUDE))
    started = time.monotonic()
    # From the block device = NTFS-3G mode (full NTFS metadata). Not --no-acls (ACLs are needed), not --snapshot
    # (Windows only); --check adds an integrity table that DISM can verify.
    run(["wimlib-imagex", "capture", partition, cfg.partial, cfg.wim_name, cfg.wim_description,
         "--compress=LZX", "--check", f"--config={config}"], capture=False, env=WIM_ENV)
    log(f"captured in {(time.monotonic() - started) / 60:.0f} min: {os.path.getsize(cfg.partial) / GIB:.1f} GiB")


def extract_hive(wim, directory):
    os.makedirs(directory)
    run(["wimlib-imagex", "extract", wim, "1", SYSTEM_HIVE, f"--dest-dir={directory}"], env=WIM_ENV)
    names = os.listdir(directory)
    if len(names) != 1:
        raise Failure(f"{SYSTEM_HIVE} was not extracted from {wim}")
    return os.path.join(directory, names[0])


def check_hive_clean(path):
    """
    The two sequence numbers of a hive differ while some of its changes are only in SYSTEM.LOG1/LOG2; hivex does not
    replay the logs, so editing such a hive would lose them. seal-golden.ps1 shuts the golden down cleanly: a dirty
    hive means the snapshot was taken of a running or crashed Windows.
    """
    with open(path, "rb") as f:
        header = f.read(12)
    if header[:4] != b"regf":
        raise Failure(f"{path} is not a registry hive")
    first, second = struct.unpack("<II", header[4:12])
    if first != second:
        raise Failure(f"the SYSTEM hive is dirty (sequence {first} != {second}): publish a version from a cleanly shut down golden")


def child(h, node, name):
    """Subkey by name, case-insensitively as Windows matches it, or None."""
    return next((c for c in h.node_children(node) if h.node_name(c).lower() == name.lower()), None) if node else None


def values(h, node):
    return {h.value_key(v).lower(): h.value_value(v) for v in h.node_values(node)} if node else {}  # name -> (type, bytes)


def dword(h, node, name):
    kind, data = values(h, node).get(name.lower(), (None, b""))
    return struct.unpack("<I", data[:4])[0] if kind == REG_DWORD and len(data) >= 4 else None


def text(value):
    kind, data = value or (None, b"")
    return data.decode("utf-16-le", "replace").split("\x00", 1)[0] if kind in (REG_SZ, REG_EXPAND_SZ) else ""


def set_start(h, node, service, start):
    current = dword(h, node, "Start")
    if current == start:
        log(f"  {service}: Start = {start}")
        return 0
    key = next((h.value_key(v) for v in h.node_values(node) if h.value_key(v).lower() == "start"), "Start")
    h.node_set_value(node, {"key": key, "t": REG_DWORD, "value": struct.pack("<I", start)})
    log(f"  {service}: Start {current} -> {start}")
    return 1


def remove_value(h, node, name):
    """hivex cannot delete a single value: the node's values are written again without it."""
    kept = []
    for v in h.node_values(node):
        if h.value_key(v).lower() != name.lower():
            kind, data = h.value_value(v)
            kept.append({"key": h.value_key(v), "t": kind, "value": data})
    if len(kept) == len(h.node_values(node)):
        return False
    h.node_set_values(node, kept)
    return True


def nic_services(h, control_set):
    """Driver services of every PCI network card the image has seen (Enum\\PCI\\<device>\\<instance>, class Net)."""
    found = {}
    pci = child(h, child(h, control_set, "Enum"), "PCI")
    for device in h.node_children(pci) if pci else []:
        for instance in h.node_children(device):
            entry = values(h, instance)
            service = text(entry.get("service"))
            if text(entry.get("classguid")).lower() == NET_CLASS and service:
                found[service.lower()] = service
    return found


def current_control_set(h):
    """(name, node, its Services node) of the control set Select\\Current names."""
    root = h.root()
    current = dword(h, child(h, root, "Select"), "Current")
    control_set = child(h, root, f"ControlSet{current:03d}") if current else None
    services = child(h, control_set, "Services")
    if services is None:
        raise Failure("no Select\\Current control set with Services in the SYSTEM hive")
    return f"ControlSet{current:03d}", control_set, services


def check_hive(path):
    """Before the capture, on a copy from the partition: a clean hive with the services fix_system_hive sets."""
    check_hive_clean(path)
    h = hivex.Hivex(path)
    set_name, _, services = current_control_set(h)
    needed = (*STORAGE_SERVICES, "MSiSCSI")
    missing = [s for s in needed if child(h, services, s) is None]
    if missing:
        raise Failure(f"service(s) {', '.join(missing)} missing in {set_name} of the image")
    log(f"  SYSTEM hive: clean, {set_name} has {', '.join(needed)}")


def fix_system_hive(path):
    """Offline fixes for booting the diskless golden from a local SSD; returns the number of changes."""
    h = hivex.Hivex(path, write=True)
    set_name, control_set, services = current_control_set(h)
    log(f"control set {set_name} (Select\\Current)")
    changes = 0

    # Windows installed on the iSCSI disk has these storage drivers on demand (StartOverride\0 = 3): 0x7B on AHCI/NVMe.
    for name in STORAGE_SERVICES:
        node = child(h, services, name)
        if node is None:
            raise Failure(f"service {name} is missing in the image")
        changes += set_start(h, node, name, 0)
        override = child(h, node, "StartOverride")
        if override:
            h.node_delete_child(override)
            log(f"  {name}: StartOverride deleted")
            changes += 1

    names = {n.lower(): n for n in BOOT_START_SERVICES} | nic_services(h, control_set)
    for name in sorted(names.values(), key=str.lower):
        node = child(h, services, name)
        if node is None:
            continue
        if remove_value(h, node, "BootFlags"):
            log(f"  {name}: BootFlags deleted")
            changes += 1
        if dword(h, node, "Start") == 0:
            changes += set_start(h, node, name, 3)
        else:
            log(f"  {name}: Start = {dword(h, node, 'Start')}")
        if child(h, node, "StartOverride"):
            log(f"  WARNING: {name} has a StartOverride subkey (not made by the club scripts, left as is)")

    node = child(h, services, "MSiSCSI")
    if node is None:
        raise Failure("service MSiSCSI is missing in the image")
    changes += set_start(h, node, "MSiSCSI", 2)  # automatic: the helper waits for it to mount G:
    if changes:
        h.commit(None)
    return changes


def state_files(wim):
    """The helper's per-machine state files in the WIM: a reference must not carry the master PC's registration."""
    rc, out = run(["wimlib-imagex", "dir", wim, "1", f"--path={STATE_DIR}"], check=False, env=WIM_ENV)
    if rc != 0:
        return []  # no ClubDiskless directory in the image
    return [p for p in (line.strip() for line in out.splitlines())
            if os.path.dirname(p).lower() == STATE_DIR.lower() and os.path.basename(p).lower().startswith(STATE_FILES)]


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def fix_wim(cfg, workdir):
    hive = extract_hive(cfg.partial, os.path.join(workdir, "hive"))
    check_hive_clean(hive)
    changes = fix_system_hive(hive)
    check_hive_clean(hive)
    leftovers = state_files(cfg.partial)
    for path in leftovers:
        log(f"  {path}: deleted from the WIM")
    if any(re.search(r"[\s'\"]", p) for p in [hive, *leftovers]):
        raise Failure(f"unexpected characters in a WIM update path: {[hive, *leftovers]}")  # commands are not quoted
    commands = ([f"add {hive} {SYSTEM_HIVE}"] if changes else []) + [f"delete --force {p}" for p in leftovers]
    if commands:
        # One update = one metadata rewrite; the integrity table stays (no --check: it would first re-read the whole
        # WIM, the final verify does that). The replaced SYSTEM gets no security descriptor of its own in the WIM: DISM
        # applies it with the ACL inherited from Windows\System32\config, as the stock hive files have.
        run(["wimlib-imagex", "update", cfg.partial, "1"], stdin="\n".join(commands) + "\n", env=WIM_ENV)
    if changes and sha256(extract_hive(cfg.partial, os.path.join(workdir, "check"))) != sha256(hive):
        raise Failure(f"{SYSTEM_HIVE} in the WIM is not the edited hive after wimlib-imagex update")
    if state_files(cfg.partial):
        raise Failure("helper state files are still in the WIM after wimlib-imagex update")
    log(f"{changes} registry change(s), {len(leftovers)} state file(s) removed")


def finish(cfg):
    run(["wimlib-imagex", "verify", cfg.partial], capture=False, env=WIM_ENV)
    shown = ("Name", "Description", "Total Bytes", "File Count", "Product Name", "Edition ID", "Build", "Default Language", "Compression", "Attributes")
    for line in run(["wimlib-imagex", "info", cfg.partial, "1"], env=WIM_ENV)[1].splitlines():
        if line.split(":", 1)[0].strip() in shown:
            log("  " + line.strip())
    st = os.stat(cfg.incoming)
    os.chown(cfg.partial, st.st_uid, st.st_gid)  # like the share's files: the server moves it on import
    os.chmod(cfg.partial, 0o640)
    if os.path.exists(cfg.wim):
        cfg.keep_partial = True  # verified and complete: not deleted by the failure cleanup
        raise Failure(f"{cfg.wim} appeared meanwhile: not overwritten. The verified result stays in {cfg.partial}; "
                      f"rename it to <another label>.wim to import it (a --cleanup would delete it)")
    os.rename(cfg.partial, cfg.wim)
    log(f"sha256 of {cfg.wim} …")
    digest = sha256(cfg.wim)
    log()
    log(f"OK: {cfg.wim}")
    log(f"    {os.path.getsize(cfg.wim)} bytes ({os.path.getsize(cfg.wim) / GIB:.2f} GiB), sha256 {digest}")
    log(f"Next: panel «Образы Windows» → «Импортировать»: file {os.path.basename(cfg.wim)}, label {cfg.label}, index 1.")
    log("Do NOT publish it: it is not generalized — reimage only the reference PC with it (docs/imaging.md).")


# ---- main -----------------------------------------------------------------------------------------------------------

def read_vm_iqn():
    with open(INITIATOR_FILE, encoding="utf-8") as f:
        names = [line.split("=", 1)[1].strip() for line in f if line.strip().startswith("InitiatorName=")]
    if not names or not names[0].startswith("iqn."):
        raise Failure(f"no InitiatorName in {INITIATOR_FILE}")
    return names[0]


def stale_partials(cfg):
    """
    Unfinished WIMs of an interrupted run: <label>.wim.partial; with no label (a plain --cleanup) every <label>.wim.partial
    in incoming. Nothing else writes one there (the server's import goes through images/<label>/, capture-hybrid.ps1
    through incoming/capture-tmp/), and the lock keeps a running make-hybrid-ref from losing its own.
    """
    if cfg.partial:
        return [cfg.partial] if os.path.isfile(cfg.partial) else []
    paths = glob.glob(os.path.join(glob.escape(cfg.incoming), "*.wim.partial"))
    return sorted(p for p in paths if os.path.isfile(p) and LABEL.match(os.path.basename(p)[:-len(".wim.partial")]))


def main():
    parser = argparse.ArgumentParser(description="Reference WIM of the hybrid image from a published diskless version (docs/imaging.md).")
    parser.add_argument("version", nargs="?", help="published diskless version, e.g. v2-2026-10-22 (snapshot <image zvol>@img-<version>)")
    parser.add_argument("--label", help="label for the panel import, also the file name in incoming (default: ref-<version>)")
    parser.add_argument("--cleanup", action="store_true",
                        help="only remove what an interrupted run left in TrueNAS and on this host, its unfinished "
                             "incoming/<label>.wim.partial included (with no version or --label: every *.wim.partial there)")
    parser.add_argument("--selftest-wof", nargs=3, metavar=("REPARSE", "STREAM", "SIZE"),
                        help="only decode one WOF sample offline (reparse data in hex or a file, its WofCompressedData, "
                             "the file size) and print its sha256; no root, no TrueNAS")
    parser.add_argument("--out", help="with --selftest-wof: write the decoded data to this file")
    args = parser.parse_args()
    if args.selftest_wof:
        try:
            return selftest_wof(*args.selftest_wof, out=args.out)
        except (Failure, OSError, ValueError) as error:
            sys.exit(f"ERROR: {error}")
    if not args.cleanup and not args.version:
        parser.error("the version is required (or --cleanup)")
    if args.version and not LABEL.match(args.version):
        parser.error(f"unexpected version '{args.version}'")
    label = args.label or (f"ref-{args.version}" if args.version else None)
    if label and not LABEL.match(label):
        parser.error(f"label '{label}' must match {LABEL.pattern} (ImageLibrary.LabelPattern)")
    if os.geteuid() != 0:
        sys.exit("run as root: sudo python3 make-hybrid-ref.py …")
    missing = [f"{tool} (sudo apt install {package})" for tool, package in (
        ("wimlib-imagex", "wimtools"), ("ntfs-3g", "ntfs-3g"), ("iscsiadm", "open-iscsi"), ("lsblk", "util-linux"),
        ("blkid", "util-linux"), ("blockdev", "util-linux"), ("udevadm", "udev")) if shutil.which(tool) is None]
    missing += ["Python module websocket (sudo apt install python3-websocket)"] if websocket is None else []
    missing += ["Python module hivex (sudo apt install python3-hivex)"] if hivex is None else []
    try:
        codec = Libwim()
    except Failure as error:
        missing.append(str(error))
    if missing:
        sys.exit("missing:\n  " + "\n  ".join(missing))
    lock = open(LOCK_FILE, "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        sys.exit("another make-hybrid-ref.py is running")

    try:
        env = read_env(ENV_FILE)
        portal = env.get("Library__DiscoveryAddress") or env.get("Library__PortalAddress") or ""
        if not portal:
            raise Failure(f"Library__PortalAddress is not set in {ENV_FILE}")
        image_zvol = env.get("Diskless__ImageZvol") or "ssd/club/diskless/win11"
        incoming = os.path.join(env.get("Imaging__Root") or "/srv/club/images", "incoming")
        cfg = SimpleNamespace(
            portal=portal if ":" in portal else portal + ":3260", portal_id=int(env.get("Library__PortalId") or 1),
            image_zvol=image_zvol, clone=f"{image_zvol.rsplit('/', 1)[0]}/{TMP}", vm_iqn=read_vm_iqn(), iqn=None,
            snapshot=f"{image_zvol}@{SNAPSHOT_PREFIX}{args.version}", label=label, incoming=incoming,
            wim=os.path.join(incoming, f"{label}.wim") if label else None,
            partial=os.path.join(incoming, f"{label}.wim.partial") if label else None, keep_partial=False,
            wim_name=f"Club hybrid reference {args.version}",
            wim_description=f"{image_zvol}@{SNAPSHOT_PREFIX}{args.version}, not generalized, fixed for a local SSD, {time.strftime('%Y-%m-%d')}")
        if not args.cleanup:
            if not os.path.isdir(incoming):
                raise Failure(f"{incoming} does not exist (is the images volume mounted?)")
            if not os.path.ismount(os.path.dirname(incoming)):
                log(f"WARNING: {os.path.dirname(incoming)} is not a separate volume: the WIM goes to this VM's own disk")
            if os.path.exists(cfg.wim) or os.path.exists(os.path.join(os.path.dirname(incoming), "images", label)):
                raise Failure(f"{cfg.wim} or an imported image '{label}' already exists: choose another --label")
        tn = TrueNas(env)
    except (Failure, OSError) as error:
        sys.exit(f"ERROR: {error}")

    for s in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(s, on_signal)
    workdir = tempfile.mkdtemp(prefix="make-hybrid-ref.", dir="/var/tmp")  # 0700: holds a copy of the SYSTEM hive
    started = time.monotonic()
    ok = False
    cleanup_pending = False  # the temporary objects are not all removed after the capture: retried at the end
    try:
        step("1/7 leftovers of an earlier run")
        for path in stale_partials(cfg):  # first: needs no TrueNAS
            log(f"delete {path}")
            os.remove(path)
        cfg.iqn = f"{tn.call('iscsi.global.config')['basename']}:{TMP}"
        cleanup(tn, cfg)
        if args.cleanup:
            ok = True
            log("OK: nothing of make-hybrid-ref is left")
            return 0
        step("2/7 temporary clone and iSCSI target in TrueNAS")
        expose(tn, cfg)
        tn.close()
        step("3/7 iSCSI login from this host, Windows partition")
        disk = login(cfg)
        partition, _ = find_windows(disk, workdir)  # the used bytes are measured again after the WOF step
        step("4/7 system-compressed (WOF) files on the clone made plain files")
        used = decompress_wof(disk, partition, workdir, codec)
        step(f"5/7 capture into {cfg.partial}")
        capture(cfg, partition, used, workdir)
        check_wim_no_wof(cfg.partial)
        step("6/7 remove the temporary objects")
        try:
            cleanup(tn, cfg)
        except Interrupted:
            raise
        except Exception as error:
            # The WIM is complete and does not depend on them (e.g. a failed SCST reload keeps the zvol busy).
            cleanup_pending = True
            log(f"WARNING: the temporary objects are not all removed: {error}")
            log("         the WIM is kept and finished; the removal is tried again at the end")
        tn.close()
        step("7/7 offline registry fixes for a local SSD, inside the WIM")
        fix_wim(cfg, workdir)
        step("check the WIM")
        finish(cfg)
        ok = True
    except (Failure, RpcError, Interrupted) as error:
        log(f"ERROR: {error}")
    except Exception:
        log("ERROR: " + traceback.format_exc().strip())
    finally:
        for s in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            signal.signal(s, signal.SIG_IGN)  # the cleanup must not be cut short
        if (not ok or cleanup_pending) and cfg.iqn:
            step("cleanup after the failure" if not ok else "remove the temporary objects, second try")
            tn.close()  # a fresh connection: the failure may have been the old one
            try:
                cleanup(tn, cfg)
                cleanup_pending = False
            except Exception as error:
                cleanup_pending = True
                log(f"ERROR: cleanup did not finish: {error}")
                log("       run again: sudo python3 make-hybrid-ref.py --cleanup")
        if not ok and cfg.partial and os.path.exists(cfg.partial) and not cfg.keep_partial:
            log(f"delete {cfg.partial}")  # incomplete, or not fixed / not verified
            os.remove(cfg.partial)
        tn.close()
        shutil.rmtree(workdir, ignore_errors=True)
        result = "FAILED" if not ok else "done, but the cleanup did NOT finish" if cleanup_pending else "done"
        log(f"{result} in {(time.monotonic() - started) / 60:.0f} min (log: {LOG_FILE})")
    return 0 if ok and not cleanup_pending else 1


if __name__ == "__main__":
    sys.exit(main())

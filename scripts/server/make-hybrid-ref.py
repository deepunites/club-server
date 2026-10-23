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
#      found by a read-only ntfs-3g probe; its SYSTEM hive is checked already here (clean, the services of step 4);
#   3. wimlib-imagex capture straight from the partition (NTFS-3G mode: ACLs, named streams, reparse points, short
#      names) into <Imaging__Root>/incoming/<label>.wim.partial; logout, everything created in TrueNAS removed (if
#      that fails, only a warning: the WIM is finished and the removal tried again at the end);
#   4. the SYSTEM hive extracted from the WIM and edited with hivex in the Select\Current control set: storahci and
#      stornvme boot-start without StartOverride; the NIC/iSCSI services the diskless preparation made boot-start
#      (Start=0, BootFlags=1: install.cmd of extract-winsetup.sh, prepare-golden.ps1) back to Start=3 without
#      BootFlags; MSiSCSI automatic. Put back with wimlib-imagex update, together with the removal of the helper's
#      per-machine state files (credentials.bin*, assignment.json*) should any be there;
#   5. wimlib-imagex verify, rename to <label>.wim (the panel lists only top-level *.wim), size and sha256.
# A failure deletes the .partial, except a verified one whose final name was taken meanwhile.
# Needs: wimtools, ntfs-3g, open-iscsi, python3-websocket, python3-hivex.
import argparse
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
    `scstadmin -force` reloads (TargetVerifier.cs); the clone is disposable, and this host only reads it."""
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
    run(["blockdev", "--setro", disk])  # partitions follow the disk's flag; nothing on this host may write to the clone
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
    args = parser.parse_args()
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
        step("1/6 leftovers of an earlier run")
        for path in stale_partials(cfg):  # first: needs no TrueNAS
            log(f"delete {path}")
            os.remove(path)
        cfg.iqn = f"{tn.call('iscsi.global.config')['basename']}:{TMP}"
        cleanup(tn, cfg)
        if args.cleanup:
            ok = True
            log("OK: nothing of make-hybrid-ref is left")
            return 0
        step("2/6 temporary clone and iSCSI target in TrueNAS")
        expose(tn, cfg)
        tn.close()
        step("3/6 iSCSI login from this host, Windows partition")
        partition, used = find_windows(login(cfg), workdir)
        step(f"4/6 capture into {cfg.partial}")
        capture(cfg, partition, used, workdir)
        step("5/6 remove the temporary objects")
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
        step("6/6 offline registry fixes for a local SSD, inside the WIM")
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

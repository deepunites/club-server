#!/usr/bin/env python3
# Tests of the WOF ("system compression") step of make-hybrid-ref.py (docs/imaging.md, "Эталонный WIM гибрида"):
#   python3 test-wof.py [-v]          dev box: what needs libwim or FUSE is skipped when missing
#   sudo python3 test-wof.py -v       club VM: everything, with libwim's own XPRESS/LZX compressors
# Pure Python: the reparse data, the chunk table (both widths), damaged streams, the parsing of wimlib-imagex dir
# --detailed, --selftest-wof. With libwim.so.15: round trips through wimlib_compress / wimlib_decompress for the four
# WOF formats. With ntfs-3g, mkntfs and FUSE: a small NTFS image with hand-made WOF files (zlib standing in for libwim
# where it is missing: same chunk framing), converted and checked by the script's own functions on real ntfs-3g mounts.
import contextlib
import ctypes
import hashlib
import importlib.util
import io
import os
import random
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import time
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("make_hybrid_ref", os.path.join(HERE, "make-hybrid-ref.py"))
mhr = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mhr)

LOG = []
mhr.log = lambda text="": LOG.append(text)  # also keeps the tests out of /var/log/make-hybrid-ref.log
CHUNK = {0: 4096, 1: 32768, 2: 8192, 3: 16384}  # written down again on purpose: the script's table is checked too
TAG = 0x80000017
EIGHT_BYTE_SIZE = 0x100000000  # first size with 8-byte offsets


def reparse(fmt, provider=2):
    return struct.pack("<IHHIIII", TAG, 16, 0, 1, provider, 1, fmt)


def sample(size, seed):
    """Mixed data: text (compresses), zeros, random bytes (stays raw)."""
    rnd = random.Random(seed)
    words = [b"kernel32", b"Microsoft", b"Windows", b"\x00\x00\x00\x00", b"MZ\x90\x00", b"servicing", b" "]
    out = bytearray()
    while len(out) < size:
        kind, length = rnd.randrange(3), rnd.randrange(1, 20000)
        if kind == 0:
            out += b"".join(rnd.choice(words) for _ in range(length // 6 + 1))
        elif kind == 1:
            out += bytes(length)
        else:
            out += rnd.randbytes(length)
    return bytes(out[:size])


def build_stream(data, fmt, compress=None):
    """WofCompressedData of data, written independently of the script: (stream, chunk count, chunks kept raw)."""
    chunk = CHUNK[fmt]
    chunks, raw = [], 0
    for offset in range(0, len(data), chunk):
        piece = data[offset:offset + chunk]
        packed = compress(fmt, piece) if compress else None
        if packed is None or len(packed) >= len(piece):
            packed, raw = piece, raw + 1
        chunks.append(packed)
    width = "<I" if len(data) <= 0xFFFFFFFF else "<Q"
    table, position = b"", 0
    for packed in chunks[:-1]:
        position += len(packed)
        table += struct.pack(width, position)
    return table + b"".join(chunks), len(chunks), raw


class ZlibCodec:
    """Stand-in for libwim: another compression, the same framing (a chunk is raw iff stored size == size)."""

    def compress(self, fmt, data):
        return zlib.compress(data, 9)

    def decompress(self, fmt, data, size):
        try:
            out = zlib.decompress(data)
        except zlib.error as error:
            raise mhr.Failure(f"zlib: {error}") from None
        if len(out) != size:
            raise mhr.Failure(f"zlib: {len(out)} bytes instead of {size}")
        return out


class LibwimCodec:
    """libwim's own compressors (wimlib_create_compressor / wimlib_compress) and the script's decompressor."""

    def __init__(self):
        self.wim = mhr.Libwim()
        lib = self.wim.lib
        lib.wimlib_create_compressor.argtypes = [ctypes.c_int, ctypes.c_size_t, ctypes.c_uint, ctypes.POINTER(ctypes.c_void_p)]
        lib.wimlib_create_compressor.restype = ctypes.c_int
        lib.wimlib_compress.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p]
        lib.wimlib_compress.restype = ctypes.c_size_t
        self.compressors = {}
        self.out = ctypes.create_string_buffer(32768)

    def compress(self, fmt, data):
        handle = self.compressors.get(fmt)
        if handle is None:
            handle = ctypes.c_void_p()
            ctype = 2 if fmt == 1 else 1  # LZX, XPRESS
            rc = self.wim.lib.wimlib_create_compressor(ctype, CHUNK[fmt], 0, ctypes.byref(handle))
            assert rc == 0, f"wimlib_create_compressor: {rc}"
            self.compressors[fmt] = handle
        size = self.wim.lib.wimlib_compress(data, len(data), self.out, len(data) - 1, handle)  # 0: would not shrink
        return ctypes.string_at(self.out, size) if size else None

    def decompress(self, fmt, data, size):
        return self.wim.decompress(fmt, data, size)


try:
    LIBWIM = LibwimCodec()
except mhr.Failure:
    LIBWIM = None


def decode(stream, size, fmt, codec):
    out = io.BytesIO()
    digest, chunks, raw = mhr.wof_decode(io.BytesIO(stream), size, fmt, len(stream), out, codec.decompress)
    return out.getvalue(), digest, chunks, raw


class ReparseTests(unittest.TestCase):
    def test_formats(self):
        self.assertEqual({k: v[1] for k, v in mhr.WOF_FORMATS.items()}, CHUNK)
        for fmt in CHUNK:
            self.assertEqual(mhr.parse_wof_reparse(reparse(fmt)), fmt)

    def test_wimboot_refused(self):
        data = struct.pack("<IHH", TAG, 44, 0) + struct.pack("<IIII", 1, 1, 1, 0) + bytes(28)
        with self.assertRaisesRegex(mhr.Failure, "WIMBoot"):
            mhr.parse_wof_reparse(data)

    def test_unexpected(self):
        cases = [struct.pack("<IHHIIII", 0xA000000C, 16, 0, 1, 2, 1, 0),  # another tag
                 struct.pack("<IHHIIII", TAG, 16, 0, 2, 2, 1, 0),         # WOF version
                 struct.pack("<IHHIIII", TAG, 16, 0, 1, 3, 1, 0),         # provider
                 struct.pack("<IHHIIII", TAG, 16, 0, 1, 2, 2, 0),         # FILE provider version
                 struct.pack("<IHHIIII", TAG, 16, 0, 1, 2, 1, 4),         # format
                 struct.pack("<IHHIIII", TAG, 12, 0, 1, 2, 1, 0),         # length field
                 reparse(0) + b"\0", reparse(0)[:12]]
        for data in cases:
            with self.assertRaises(mhr.Failure, msg=data.hex()):
                mhr.parse_wof_reparse(data)


class ChunkTableTests(unittest.TestCase):
    def test_round_trip(self):
        for fmt, chunk in CHUNK.items():
            for size in (0, 1, 100, chunk - 1, chunk, chunk + 1, 3 * chunk, 3 * chunk + 17):
                data = sample(size, size + fmt)
                for codec, compress in ((ZlibCodec(), ZlibCodec().compress), (ZlibCodec(), None)):
                    stream, count, raw = build_stream(data, fmt, compress)
                    out, digest, chunks, stored_raw = decode(stream, size, fmt, codec)
                    self.assertEqual(out, data, (fmt, size))
                    self.assertEqual(digest, hashlib.sha256(data).hexdigest())
                    self.assertEqual((chunks, stored_raw), (count, raw))
                    self.assertEqual(chunks, -(-size // chunk))

    def test_offset_width(self):
        self.assertEqual(mhr.wof_layout(0xFFFFFFFF, 1)[2], 4)
        self.assertEqual(mhr.wof_layout(EIGHT_BYTE_SIZE, 1)[2], 8)
        self.assertEqual(mhr.wof_layout(0, 0), (4096, 0, 4, 0))
        self.assertEqual(mhr.wof_layout(4097, 0), (4096, 2, 4, 4))

    def test_eight_byte_table(self):
        size = EIGHT_BYTE_SIZE + 5  # 131073 LZX chunks, the last one 5 bytes; every chunk stored as 1 byte
        _, count, width, table_len = mhr.wof_layout(size, 1)
        self.assertEqual((count, width, table_len), (131073, 8, 131072 * 8))
        table = b"".join(struct.pack("<Q", i) for i in range(1, count))
        chunks = mhr.wof_chunks(size, 1, table_len + count, table)
        self.assertEqual(len(chunks), count)
        self.assertEqual(chunks[0], (1, 32768))
        self.assertEqual(chunks[-1], (1, 5))
        with self.assertRaises(mhr.Failure):  # the same offsets read as u32 would not fit
            mhr.wof_chunks(EIGHT_BYTE_SIZE - 1, 1, table_len + count, table)

    def test_damaged(self):
        fmt, size = 0, 3 * 4096 + 10
        data = sample(size, 7)
        stream, _, _ = build_stream(data, fmt, ZlibCodec().compress)
        table_len = 3 * 4
        starts = list(struct.unpack("<3I", stream[:table_len]))

        def with_table(values):
            return struct.pack("<3I", *values) + stream[table_len:]
        cases = {
            "offsets not increasing": with_table([starts[1], starts[0], starts[2]]),
            "zero-length chunk": with_table([0, starts[1], starts[2]]),
            "offset past the end": with_table([starts[0], starts[1], len(stream)]),
            "truncated": stream[:-1],
            "trailing data": None,
            "table only": stream[:table_len],
            "shorter than the table": stream[:5],
            "bad compressed data": stream[:table_len] + bytes(len(stream) - table_len),
        }
        for name, damaged in cases.items():
            with self.subTest(name):
                with self.assertRaises(mhr.Failure):
                    if damaged is None:  # the csize the caller knows is right, the stream goes on
                        mhr.wof_decode(io.BytesIO(stream + b"x"), size, fmt, len(stream), io.BytesIO(), ZlibCodec().decompress)
                    else:
                        decode(damaged, size, fmt, ZlibCodec())
        with self.assertRaises(mhr.Failure):  # an empty file with data
            decode(b"x", 0, fmt, ZlibCodec())
        with self.assertRaises(mhr.Failure):  # a chunk stored larger than its data
            mhr.wof_chunks(10, 0, 11, b"")

    def test_wrong_length_from_decompressor(self):
        data = sample(5000, 3)
        stream, _, _ = build_stream(data, 0, ZlibCodec().compress)
        short = type("Short", (), {"decompress": lambda self, fmt, d, size: zlib.decompress(d)[:-1]})()
        with self.assertRaisesRegex(mhr.Failure, "instead of"):
            decode(stream, len(data), 0, short)


class WimDirTests(unittest.TestCase):
    # Format of wimlib's programs/imagex.c print_dentry_detailed.
    ENTRY = ('----------------------------------------------------------------------------\n'
             'Full Path           = "{path}"\n'
             'Attributes          = 0x{attr:08x}\n'
             '    FILE_ATTRIBUTE_ARCHIVE is set\n'
             'Security Descriptor = 010004800000\n'
             'Creation Time       = Wed Oct 22 10:00:00 2026 UTC\n'
             '{tag}'
             'Link Group ID       = 0x0000000000000001\n'
             'Link Count          = 1\n'
             '{streams}\n')

    def entry(self, path, tag=None, streams=()):
        return self.ENTRY.format(path=path, attr=0x420 if tag else 0x20,
                                 tag=f"Reparse Tag         = 0x{tag:x}\n" if tag else "",
                                 streams="".join(f'\tNamed data stream "{s}":\nHash              = 0x00\n' for s in streams)
                                 + "\tUnnamed data stream:\nHash              = 0x00\nUncompressed size = 5 bytes\n")

    def test_report(self):
        text = (self.entry("\\Documents and Settings", 0xA0000003) + self.entry("\\Windows\\a.dll")
                + self.entry("\\Windows\\b.cat", TAG, ["WofCompressedData"]) + self.entry("\\c.txt", None, ["Zone.Identifier"])
                + self.entry("\\Windows\\d.dll", None, ["wofcompresseddata"]))
        entries, tags, tagged, streams = mhr.wim_wof_report(io.StringIO(text))
        self.assertEqual(entries, 5)
        self.assertEqual(dict(tags), {0xA0000003: 1, TAG: 1})
        self.assertEqual(tagged, ["\\Windows\\b.cat"])
        self.assertEqual(streams, ["\\Windows\\b.cat", "\\Windows\\d.dll"])

    def test_clean(self):
        entries, tags, tagged, streams = mhr.wim_wof_report(io.StringIO(self.entry("\\x", 0xA000000C) + self.entry("\\y")))
        self.assertEqual((entries, dict(tags), tagged, streams), (2, {0xA000000C: 1}, [], []))


class SelftestTests(unittest.TestCase):
    def test_selftest(self):
        data = sample(70000, 11)
        stream, count, _ = build_stream(data, 3, ZlibCodec().compress)
        with tempfile.TemporaryDirectory() as tmp:
            files = {name: os.path.join(tmp, name) for name in ("rp.hex", "rp.bin", "stream", "out")}
            with open(files["rp.hex"], "w") as f:
                f.write(reparse(3).hex() + "\n")
            with open(files["rp.bin"], "wb") as f:
                f.write(reparse(3)[8:])  # 16 bytes, as a WIM stores them
            with open(files["stream"], "wb") as f:
                f.write(stream)
            for rp in (files["rp.hex"], files["rp.bin"], reparse(3).hex()):
                printed = io.StringIO()
                with contextlib.redirect_stdout(printed):
                    self.assertEqual(mhr.selftest_wof(rp, files["stream"], str(len(data)), out=files["out"], codec=ZlibCodec()), 0)
                self.assertIn(f"sha256 {hashlib.sha256(data).hexdigest()}", printed.getvalue())
                self.assertIn(f"XPRESS16K: {count} chunk(s)", printed.getvalue())
                with open(files["out"], "rb") as f:
                    self.assertEqual(f.read(), data)
            with self.assertRaises(mhr.Failure):
                mhr.selftest_wof(reparse(3).hex(), files["stream"], str(len(data) + 1), codec=ZlibCodec())


@unittest.skipUnless(LIBWIM, "libwim.so.15 not found (on the club VM it comes with wimtools)")
class LibwimTests(unittest.TestCase):
    def test_round_trip(self):
        for fmt, chunk in CHUNK.items():
            for size in (1, chunk, 5 * chunk + 1234):
                data = sample(size, fmt * 7 + size)
                stream, count, raw = build_stream(data, fmt, LIBWIM.compress)
                if size > chunk:
                    self.assertLess(raw, count, "some chunks must really be compressed")
                out, digest, chunks, stored_raw = decode(stream, size, fmt, LIBWIM)
                self.assertEqual(out, data, (fmt, size))
                self.assertEqual((chunks, stored_raw), (count, raw))

    def test_fresh_decompressor_object(self):
        data = sample(100000, 5)
        stream, _, _ = build_stream(data, 1, LIBWIM.compress)
        self.assertEqual(decode(stream, len(data), 1, mhr.Libwim())[0], data)

    def test_damaged(self):
        data = b"Windows " * 4096
        for fmt in CHUNK:
            stream, count, raw = build_stream(data, fmt, LIBWIM.compress)
            self.assertEqual(raw, 0)
            table_len = (count - 1) * 4
            damaged = stream[:table_len] + b"\xff" * (len(stream) - table_len)  # XPRESS: incomplete code; LZX: block type 7
            with self.assertRaises(mhr.Failure, msg=fmt):
                decode(damaged, len(data), fmt, LIBWIM)


# ---- on real ntfs-3g mounts -----------------------------------------------------------------------------------------

def fuse_usable():
    tools = all(shutil.which(t) for t in ("ntfs-3g", "mkntfs")) and os.path.exists("/dev/fuse")
    return tools and (os.geteuid() == 0 or shutil.which("fusermount3"))


class Mounts:
    """
    ntfs-3g in the foreground (no_detach), so the test knows when it has finished writing: an image file is a plain
    "fuse" mount, whose unmount does not wait for the daemon (the script's block device mount, fuseblk, does).
    """

    def __init__(self):
        self.procs = {}

    def mount(self, device, mountpoint, options):
        proc = subprocess.Popen(["ntfs-3g", "-o", options + ",no_detach", device, mountpoint],
                                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 20
        while not os.path.ismount(mountpoint):
            if proc.poll() is not None or time.monotonic() > deadline:
                proc.kill()
                raise mhr.Failure(f"ntfs-3g -o {options} {device}: not mounted (exit {proc.poll()})")
            time.sleep(0.05)
        self.procs[os.path.realpath(mountpoint)] = proc

    def unmount(self, mountpoint):
        subprocess.run(["umount", mountpoint] if os.geteuid() == 0 else ["fusermount3", "-u", mountpoint], check=True)
        proc = self.procs.pop(os.path.realpath(mountpoint))
        proc.wait(60)
        if proc.returncode:
            raise mhr.Failure(f"ntfs-3g exited with {proc.returncode}")

    @contextlib.contextmanager
    def mounted(self, device, mountpoint, options):
        self.mount(device, mountpoint, options)
        try:
            yield mountpoint
        finally:
            self.unmount(mountpoint)


def get(path, name):
    return os.getxattr(path, name, follow_symlinks=False)


def times(*values):
    return struct.pack("=3Q", *values)


@unittest.skipUnless(fuse_usable(), "ntfs-3g, mkntfs or FUSE missing")
class Ntfs3gTests(unittest.TestCase):
    RW = "rw,noatime,streams_interface=windows"

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="test-wof.")
        self.image, self.mnt = os.path.join(self.tmp, "ntfs.img"), os.path.join(self.tmp, "mnt")
        os.makedirs(self.mnt)
        with open(self.image, "wb") as f:
            f.truncate(64 << 20)
        subprocess.run(["mkntfs", "-F", "-Q", "-q", self.image], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.mounts = Mounts()
        self.saved = mhr.mount_ntfs, mhr.unmount
        mhr.mount_ntfs, mhr.unmount = self.mounts.mount, self.mounts.unmount
        self.codec = LIBWIM or ZlibCodec()
        self.files = {}

    def tearDown(self):
        mhr.mount_ntfs, mhr.unmount = self.saved
        for mountpoint in list(self.mounts.procs):
            with contextlib.suppress(Exception):
                self.mounts.unmount(mountpoint)
        shutil.rmtree(self.tmp, ignore_errors=True)

    def make_wof(self, root, rel, data, fmt, flags=0, links=(), stream=None, provider=2, other_streams=None, attrib=None):
        """
        A WOF file as Windows leaves it: sparse unnamed stream of the full size, the stream, the reparse point. flags are
        added to the attributes ntfs-3g gave the file (ARCHIVE); attrib replaces them (0: no ARCHIVE either).
        """
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.truncate(len(data))
        for name, content in (other_streams or {}).items():
            with open(f"{path}:{name}", "wb") as f:
                f.write(content)
        with open(f"{path}:WofCompressedData", "wb") as f:
            f.write(stream if stream is not None else build_stream(data, fmt, self.codec.compress)[0])
        for link in links:
            os.makedirs(os.path.dirname(os.path.join(root, link)), exist_ok=True)
            os.link(path, os.path.join(root, link))
        if flags:
            os.setxattr(path, "system.ntfs_attrib", struct.pack("=I", struct.unpack("=I", get(path, "system.ntfs_attrib"))[0] | flags))
        if attrib is not None:
            os.setxattr(path, "system.ntfs_attrib", struct.pack("=I", attrib))
        rp = reparse(fmt, provider) if provider == 2 else (
            struct.pack("<IHH", TAG, 44, 0) + struct.pack("<IIII", 1, provider, 1, 0) + bytes(28))
        os.setxattr(path, "system.ntfs_reparse_data", rp, follow_symlinks=False)
        os.setxattr(path, "system.ntfs_times", times(132223104000000000 + len(data), 132539328000000000, 133000000000000000),
                    follow_symlinks=False)
        self.files[rel] = SimpleFile(data, links, other_streams or {})

    def state(self, root, rel):
        path = os.path.join(root, rel)
        return {"attrib": struct.unpack("=I", get(path, "system.ntfs_attrib"))[0], "times": get(path, "system.ntfs_times")[:24],
                "acl": get(path, "system.ntfs_acl")}

    def fixtures(self):
        with self.mounts.mounted(self.image, self.mnt, self.RW) as root:
            self.make_wof(root, "Windows/System32/a.dll", sample(3 * 4096 + 100, 1), 0, links=["Windows/WinSxS/amd64_a/a.dll"])
            self.make_wof(root, "Windows/System32/lzx.bin", sample(2 * 32768 + 1, 2), 1,
                          other_streams={"Zone.Identifier": b"[ZoneTransfer]\r\nZoneId=3\r\n"})
            self.make_wof(root, "Windows/WinSxS/x8k.bin", sample(2 * 8192, 3), 2)
            self.make_wof(root, "Program Files/App/x16k.bin", sample(16384 + 7, 4), 3, flags=0x1 | 0x2 | 0x4)
            self.make_wof(root, "Windows/System32/empty.bin", b"", 0)
            self.make_wof(root, "Windows/System32/small.bin", sample(100, 5), 1)
            self.make_wof(root, "Windows/System32/random.bin", os.urandom(40000), 0)  # every chunk raw
            # No attribute but the WOF ones: plain, the file has none at all, which ntfs-3g reports as NORMAL (0x80).
            self.make_wof(root, "Windows/System32/noattr.dll", sample(2 * 4096 + 5, 6), 0, attrib=0)  # REPARSE|SPARSE
            self.make_wof(root, "Windows/System32/noattr-small.bin", sample(100, 7), 1, attrib=0)  # resident: REPARSE
            other =os.path.join(root, "Windows/System32/app.exe")  # AppExecLink: another reparse point, kept
            with open(other, "wb"):
                pass
            os.setxattr(other, "system.ntfs_reparse_data", struct.pack("<IHH", 0x8000001B, 8, 0) + b"appexec!")
            with open(os.path.join(root, "Windows/notes.txt"), "wb") as f:
                f.write(b"plain")
            before = {rel: self.state(root, rel) for rel in self.files}
            before["Windows/System32"] = {"times": get(os.path.join(root, "Windows/System32"), "system.ntfs_times")[:24]}
        return before

    def test_convert_and_verify(self):
        before = self.fixtures()
        converted, used = mhr.wof_convert_volume(self.image, self.mnt, self.codec)
        self.assertEqual(len(converted), len(self.files))
        self.assertGreater(used, 0)
        self.assertGreater(mhr.wof_verify_volume(self.image, self.mnt, converted), 0)
        with self.mounts.mounted(self.image, self.mnt, "ro,streams_interface=windows") as root:
            for rel, f in self.files.items():
                for name in (rel, *f.links):
                    path = os.path.join(root, name)
                    self.assertTrue(stat.S_ISREG(os.lstat(path).st_mode), name)
                    with open(path, "rb") as g:
                        self.assertEqual(g.read(), f.data, name)
                self.assertFalse(os.path.lexists(os.path.join(root, rel) + ":WofCompressedData"), rel)
                after = self.state(root, rel)
                self.assertEqual(after["times"], before[rel]["times"], rel)
                self.assertEqual(after["acl"], before[rel]["acl"], rel)
                self.assertEqual(after["attrib"] & ~0x280, before[rel]["attrib"] & ~0x680, rel)
                if f.data:
                    self.assertFalse(after["attrib"] & 0x200, f"{rel}: still sparse")
                for name, content in f.other_streams.items():
                    with open(os.path.join(root, rel) + ":" + name, "rb") as g:
                        self.assertEqual(g.read(), content)
            for rel, wof_only in (("Windows/System32/noattr.dll", 0x600), ("Windows/System32/noattr-small.bin", 0x400)):
                self.assertEqual(before[rel]["attrib"], wof_only, rel)  # the fixture is the case it is meant to be
                self.assertEqual(self.state(root, rel)["attrib"], 0x80, rel)  # no attribute: not even ARCHIVE
            self.assertEqual(get(os.path.join(root, "Windows/System32"), "system.ntfs_times")[:24], before["Windows/System32"]["times"])
            self.assertEqual(get(os.path.join(root, "Windows/System32/app.exe"), "system.ntfs_reparse_data")[:4],
                             struct.pack("<I", 0x8000001B))
            with open(os.path.join(root, "Windows/notes.txt"), "rb") as g:
                self.assertEqual(g.read(), b"plain")
        spot = [line.strip() for line in LOG if line.startswith("    ") and "C:\\" in line]
        self.assertEqual(len(spot), 4, LOG)  # one per format, never the empty file
        self.assertIn(f"{hashlib.sha256(self.files['Windows/System32/a.dll'].data).hexdigest().upper()}  "
                      "C:\\Windows\\System32\\a.dll", spot)

    def test_wimboot_refused_before_any_change(self):
        with self.mounts.mounted(self.image, self.mnt, self.RW) as root:
            self.make_wof(root, "Windows/a.dll", sample(5000, 1), 0)
            self.make_wof(root, "Windows/wimboot.dll", sample(5000, 2), 0, provider=1)
        with self.assertRaisesRegex(mhr.Failure, "WIMBoot"):
            mhr.wof_convert_volume(self.image, self.mnt, self.codec)
        with self.mounts.mounted(self.image, self.mnt, "ro,streams_interface=windows") as root:
            self.assertEqual(get(os.path.join(root, "Windows/a.dll"), "system.ntfs_reparse_data"), reparse(0))

    def test_damaged_stream_fails(self):
        data = b"Windows " * 1536  # 3 chunks, all compressed
        stream, _, raw = build_stream(data, 0, self.codec.compress)
        self.assertEqual(raw, 0)
        with self.mounts.mounted(self.image, self.mnt, self.RW) as root:
            self.make_wof(root, "Windows/bad.dll", data, 0, stream=stream[:8] + b"\xff" * (len(stream) - 8))
        with self.assertRaisesRegex(mhr.Failure, "/Windows/bad.dll: chunk 0"):
            mhr.wof_convert_volume(self.image, self.mnt, self.codec)
        self.assertFalse(mhr.os.path.ismount(self.mnt), "unmounted after the failure")

    def test_no_wof(self):
        with self.mounts.mounted(self.image, self.mnt, self.RW) as root:
            with open(os.path.join(root, "plain.txt"), "wb") as f:
                f.write(b"x")
        converted, _ = mhr.wof_convert_volume(self.image, self.mnt, self.codec)
        self.assertEqual(converted, {})


class SimpleFile:
    def __init__(self, data, links, other_streams):
        self.data, self.links, self.other_streams = data, list(links), other_streams


if __name__ == "__main__":
    print(f"libwim: {'yes' if LIBWIM else 'no (round trips through libwim skipped, zlib stands in on ntfs-3g)'}; "
          f"ntfs-3g/FUSE: {'yes' if fuse_usable() else 'no'}", file=sys.stderr)
    unittest.main()

#!/usr/bin/env python3
"""DHCPDISCOVER с заданным MAC, option 93 (архитектура) и option 77 (user class); печатает ответ как JSON."""
import json, os, random, socket, struct, sys, time

def discover(iface, mac, arch=None, user_class=None, timeout=3.0):
    xid = random.getrandbits(32)
    chaddr = bytes.fromhex(mac.replace(":", "").replace("-", ""))
    pkt = struct.pack("!BBBBIHH4s4s4s4s16s64s128s", 1, 1, 6, 0, xid, 0, 0x8000,
                      b"\0" * 4, b"\0" * 4, b"\0" * 4, b"\0" * 4, chaddr.ljust(16, b"\0"), b"", b"")
    opts = b"\x63\x82\x53\x63" + b"\x35\x01\x01"
    if arch is not None:
        opts += b"\x5d\x02" + struct.pack("!H", arch)
    if user_class is not None:
        uc = user_class.encode()
        opts += bytes([77, len(uc)]) + uc
    opts += b"\x37\x04\x01\x03\x06\x43" + b"\xff"
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, iface.encode())
    s.bind(("0.0.0.0", 68))
    # Ответ Kea шлёт через raw-сокет на MAC клиента (chaddr) — принимаем кадры на уровне Ethernet.
    raw = socket.socket(socket.AF_PACKET, socket.SOCK_RAW, socket.htons(0x0800))
    raw.bind((iface, 0))
    raw.settimeout(timeout)
    s.sendto(pkt + opts, ("255.255.255.255", 67))
    end = time.time() + timeout
    while time.time() < end:
        try:
            frame = raw.recv(4096)
        except socket.timeout:
            break
        ihl = (frame[14] & 0x0F) * 4
        if frame[23] != 17 or struct.unpack("!H", frame[14 + ihl + 2:14 + ihl + 4])[0] != 68:
            continue
        data = frame[14 + ihl + 8:]
        if len(data) < 240 or data[0] != 2 or struct.unpack("!I", data[4:8])[0] != xid:
            continue
        raw.close()
        yiaddr = socket.inet_ntoa(data[16:20]); siaddr = socket.inet_ntoa(data[20:24])
        file = data[108:236].split(b"\0")[0].decode(errors="replace")
        options = {}; i = 240
        while i < len(data) and data[i] != 255:
            if data[i] == 0: i += 1; continue
            code, ln = data[i], data[i + 1]; options[code] = data[i + 2:i + 2 + ln]; i += 2 + ln
        s.close()
        return {"offer": True, "yiaddr": yiaddr, "siaddr": siaddr, "file": file,
                "option67": options.get(67, b"").decode(errors="replace") or None}
    s.close()
    return {"offer": False}

if __name__ == "__main__":
    iface = sys.argv[1]
    results = []
    for spec in sys.argv[2:]:
        mac, arch, uc = (spec.split(",") + ["", ""])[:3]
        results.append({"mac": mac, "arch": int(arch) if arch else None, "userClass": uc or None,
                        **discover(iface, mac, int(arch) if arch else None, uc or None)})
    print(json.dumps(results))

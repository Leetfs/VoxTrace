"""Validate that captured UDP/27002 packets are VRChat Photon traffic.

Reads a pcapng produced by `pktmon etl2pcap`, extracts UDP port 27002,
and keeps only remote IPs whose payload has the Photon header shape:

    peer_id:2  0x01  challenge:4  nonce:12  ciphertext  tag:16

If a session key is supplied (or found in bin/_pipeline.log), packets are
also authenticated with AES-256-GCM; IPs with at least one authenticated
packet are marked verified.

Usage:
    python scripts/verify_photon_pcap.py photon.pcapng [--key HEX] [--out DIR]
"""

import argparse
import ipaddress
import json
import os
import re
import struct
import sys

PORT = 27002
NAMESERVER = ipaddress.ip_network("216.120.180.0/23")

try:
    from Crypto.Cipher import AES
except Exception:
    AES = None


def blocks(data):
    off = 0
    while off + 12 <= len(data):
        btype, blen = struct.unpack_from("<II", data, off)
        if blen < 12 or off + blen > len(data):
            break
        yield btype, data[off + 8:off + blen - 4]
        off += blen


def interfaces(data):
    out = []
    for btype, body in blocks(data):
        if btype == 1 and len(body) >= 8:
            out.append(struct.unpack_from("<H", body, 0)[0])
    return out


def packets(data):
    linktypes = interfaces(data)
    for btype, body in blocks(data):
        if btype == 6 and len(body) >= 24:  # Enhanced Packet Block
            iface, _, _, caplen, _ = struct.unpack_from("<IIIII", body, 0)
            pkt = body[20:20 + caplen]
        elif btype == 3 and len(body) >= 4:  # Simple Packet Block
            iface = 0
            origlen = struct.unpack_from("<I", body, 0)[0]
            pkt = body[4:4 + origlen]
        else:
            continue
        lt = linktypes[iface] if iface < len(linktypes) else 1
        yield lt, pkt


def ipv4_udp(linktype, pkt):
    if linktype == 1:  # Ethernet
        if len(pkt) < 14:
            return None
        off = 12
        etype = struct.unpack_from(">H", pkt, off)[0]
        off = 14
        while etype in (0x8100, 0x88A8) and len(pkt) >= off + 4:
            etype = struct.unpack_from(">H", pkt, off + 2)[0]
            off += 4
        if etype != 0x0800:
            return None
    elif linktype in (101, 228):  # raw IPv4
        off = 0
    else:
        return None

    if len(pkt) < off + 20:
        return None
    ver_ihl = pkt[off]
    if (ver_ihl >> 4) != 4:
        return None
    ihl = (ver_ihl & 0x0F) * 4
    if len(pkt) < off + ihl + 8 or pkt[off + 9] != 17:
        return None
    sport, dport, ulen = struct.unpack_from(">HHH", pkt, off + ihl)
    if sport != PORT and dport != PORT:
        return None
    payload = pkt[off + ihl + 8: off + ihl + ulen]
    src = ".".join(str(x) for x in pkt[off + 12:off + 16])
    dst = ".".join(str(x) for x in pkt[off + 16:off + 20])
    remote = src if sport == PORT else dst
    return remote, payload


def photon_shape(payload):
    if len(payload) < 40 or payload[2] != 1:
        return None
    return payload


def gcm_verify(key, payload):
    if AES is None or len(payload) < 40:
        return False
    header = payload[:7]
    blob = payload[7:]
    if len(blob) < 28:
        return False
    nonce, ct, tag = blob[:12], blob[12:-16], blob[-16:]
    try:
        cipher = AES.new(key, AES.MODE_GCM, nonce=nonce)
        cipher.update(header + nonce)
        cipher.decrypt_and_verify(ct, tag)
        return True
    except Exception:
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pcapng")
    ap.add_argument("--key", default=None)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    key = None
    key_source = None
    if args.key:
        key = bytes.fromhex(args.key)
        key_source = "argument"
    else:
        log = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                           "bin", "_pipeline.log")
        if os.path.exists(log):
            text = open(log, encoding="utf-8", errors="ignore").read()
            found = re.findall(r"^key: ([0-9a-f]{64})", text, re.M)
            if found:
                key = bytes.fromhex(found[-1])
                key_source = "bin/_pipeline.log"

    data = open(args.pcapng, "rb").read()
    stats = {}
    for linktype, pkt in packets(data):
        parsed = ipv4_udp(linktype, pkt)
        if not parsed:
            continue
        remote, payload = parsed
        if ipaddress.ip_address(remote) in NAMESERVER:
            continue
        if photon_shape(payload) is None:
            continue
        st = stats.setdefault(remote, {"packets": 0, "peers": set(), "verified": 0})
        st["packets"] += 1
        st["peers"].add(payload[:2].hex())
        if key is not None and gcm_verify(key, payload):
            st["verified"] += 1

    verified = []
    structural = []
    for ip, st in sorted(stats.items()):
        item = {
            "ip": ip,
            "packets": st["packets"],
            "peers": sorted(st["peers"]),
            "gcm_verified": st["verified"],
        }
        if st["verified"] > 0:
            verified.append(item)
        elif st["packets"] >= 5:
            structural.append(item)

    accepted = verified if verified else structural
    cidrs = sorted({".".join(ip.split(".")[:3]) + ".0/24" for ip in (x["ip"] for x in accepted)})

    report = {
        "pcapng": os.path.abspath(args.pcapng),
        "key_source": key_source,
        "total_ips": len(stats),
        "verified": verified,
        "structural": structural,
        "accepted_cidrs": cidrs,
    }

    outdir = args.out or os.path.dirname(os.path.abspath(args.pcapng))
    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, "verify_report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    with open(os.path.join(outdir, "verified_cidrs.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(cidrs) + ("\n" if cidrs else ""))

    print("key source:", key_source)
    for item in verified:
        print("VERIFIED  %-15s packets=%d gcm=%d" % (item["ip"], item["packets"], item["gcm_verified"]))
    for item in structural:
        print("STRUCT    %-15s packets=%d" % (item["ip"], item["packets"]))
    print("accepted CIDR:", ", ".join(cidrs) if cidrs else "(none)")


if __name__ == "__main__":
    sys.exit(main())


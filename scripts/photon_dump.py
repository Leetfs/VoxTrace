"""Decrypt a VoxTrace capture and decode every Photon message to JSONL.

Requires the optional eventparse helper built from gui/eventparse (Photon SDK).

Usage:
    python scripts/photon_dump.py captures/voice_*.txt --key HEX --out captures/photon
"""

import argparse
import collections
import json
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from voxtrace import crypto, uspeak  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def collect(capture, key):
    frags = {}
    messages = []
    lines = decrypted = 0
    with open(capture, "r", errors="ignore") as f:
        for line in f:
            lines += 1
            result = crypto.decrypt_datagram(key, line)
            if not result:
                continue
            decrypted += 1
            for direction, msg in uspeak.iter_messages(result[1], result[0], frags):
                if len(msg) >= 2 and msg[0] == 0xF3:
                    messages.append((direction, msg))
    return messages, lines, decrypted


def summarize(records):
    events = collections.Counter()
    ops = collections.Counter()
    params = collections.defaultdict(collections.Counter)
    event_params = collections.defaultdict(collections.Counter)
    field_paths = collections.Counter()
    sizes = collections.defaultdict(list)

    def walk(value, path):
        if isinstance(value, dict):
            for k, v in value.items():
                p = path + "." + str(k) if path else str(k)
                field_paths[p] += 1
                walk(v, p)
        elif isinstance(value, list):
            for v in value[:8]:
                walk(v, path + "[]")

    for rec in records:
        sizes[rec.get("code", rec.get("op"))].append(len(json.dumps(rec)))
        if "code" in rec:
            events[rec["code"]] += 1
            p = rec.get("params", {})
            if isinstance(p, dict):
                for k in p:
                    event_params[rec["code"]][k] += 1
            walk(p, "event%d" % rec["code"])
        elif "op" in rec:
            ops[rec["op"]] += 1
            walk(rec.get("params", {}), "op%d" % rec["op"])

    return {
        "messages": len(records),
        "events": dict(events.most_common()),
        "operations": dict(ops.most_common()),
        "event_params": {str(k): dict(v.most_common()) for k, v in event_params.items()},
        "field_paths": dict(field_paths.most_common(2000)),
        "sizes_avg": {str(k): round(sum(v) / len(v), 1) for k, v in sizes.items() if v},
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("capture")
    ap.add_argument("--key", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--helper", default=os.path.join(ROOT, "bin", "eventparse", "eventparse.exe"))
    args = ap.parse_args()

    outdir = args.out or os.path.join(os.path.dirname(os.path.abspath(args.capture)), "photon")
    os.makedirs(outdir, exist_ok=True)

    key = bytes.fromhex(args.key)
    messages, lines, decrypted = collect(args.capture, key)
    if not messages:
        print("no Photon messages (wrong key?)")
        return 1

    if not os.path.isfile(args.helper):
        print("eventparse helper not found:", args.helper)
        return 1

    bin_path = os.path.join(outdir, "messages.bin")
    jsonl_path = os.path.join(outdir, "photon.jsonl")
    with open(bin_path, "wb") as f:
        for direction, msg in messages:
            d = 1 if direction == "S" else 0
            f.write(bytes([ord("S") if d else ord("C"), 0]) + struct.pack("<i", len(msg)) + msg)

    subprocess.run([args.helper, bin_path, jsonl_path], check=False)
    records = []
    with open(jsonl_path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            try:
                records.append(json.loads(line))
            except Exception:
                continue

    summary = summarize(records)
    summary["capture"] = os.path.abspath(args.capture)
    summary["lines"] = lines
    summary["decrypted"] = decrypted
    summary["messages"] = len(records)
    with open(os.path.join(outdir, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    print("lines", lines, "decrypted", decrypted, "messages", len(records))
    print("events", dict(collections.Counter(r.get("code") for r in records if "code" in r).most_common()))
    print("ops", dict(collections.Counter(r.get("op") for r in records if "op" in r).most_common()))
    print("out", outdir)
    return 0


if __name__ == "__main__":
    sys.exit(main())

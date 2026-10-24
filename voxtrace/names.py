"""Incremental actor -> {usr, displayName} extraction (Event 253).

The nested Protocol18 dictionary is decoded by an optional helper executable
built from ``gui/eventparse`` (requires the Photon .NET SDK, see
``docs/BUILD.md``).  When the helper is missing, name mapping is skipped; the GUI
still allows manual binding.
"""
import json
import os
import re
import struct
import subprocess
import time

from . import crypto, uspeak

OVERLAP = 512 * 1024


def _state(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {"offset": 0, "actors": {}, "users": {}}


def _save(path, obj):
    parent = os.path.dirname(os.path.abspath(path))
    if parent:
        os.makedirs(parent, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


def extract(cfg, capture, key, out_json, state_path=None):
    """Process newly appended capture bytes; returns (actors, users) or None."""
    state_path = state_path or (out_json + ".state.json")
    state = _state(state_path)
    actors = state.get("actors", {})
    users = state.get("users", {})
    offset = state.get("offset", 0)

    size = os.path.getsize(capture)
    start = max(0, offset - OVERLAP)
    if start >= size:
        _save(out_json, {"updated": int(time.time()), "actors": actors, "users": users})
        return actors, users

    msgs = []
    frags = {}
    with open(capture, "r", errors="ignore") as f:
        f.seek(start)
        if start > 0:
            f.readline()
        for line in f:
            result = crypto.decrypt_datagram(key, line)
            if not result:
                continue
            for _direction, msg in uspeak.iter_messages(result[1], result[0], frags):
                if len(msg) >= 3 and msg[0] == 0xF3 and msg[1] == 4 and b"displayName" in msg:
                    msgs.append(msg)

    if msgs:
        helper = cfg.get("eventparse_exe", "")
        helper = helper if os.path.isabs(helper) else os.path.join(cfg["root"], helper)
        if not os.path.isfile(helper):
            _save(out_json, {"updated": int(time.time()), "actors": actors, "users": users})
            _save(state_path, {"offset": size, "actors": actors, "users": users})
            return actors, users
        tmpdir = os.path.join(cfg["root"], "captures", "_names")
        os.makedirs(tmpdir, exist_ok=True)
        bin_path = os.path.join(tmpdir, "assembled.bin")
        json_path = os.path.join(tmpdir, "assembled.jsonl")
        with open(bin_path, "wb") as f:
            for msg in msgs:
                f.write(b"S\x00" + struct.pack("<i", len(msg)) + msg)
        env = dict(os.environ)
        if cfg.get("dotnet_root"):
            env["DOTNET_ROOT"] = cfg["dotnet_root"]
        try:
            subprocess.run([helper, bin_path, json_path], env=env, timeout=60,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        except Exception:
            pass

        actor_re = re.compile(r'(\d+):\{')
        id_re = re.compile(r'id:s:(usr_[0-9a-f-]+)')
        name_re = re.compile(r'displayName:s:([^,}]+)')
        if os.path.isfile(json_path):
            for line in open(json_path, encoding="utf-8", errors="ignore"):
                if "displayName" not in line:
                    continue
                starts = [(m.start(), int(m.group(1))) for m in actor_re.finditer(line)]
                for k, (p, actor) in enumerate(starts):
                    end = starts[k + 1][0] if k + 1 < len(starts) else len(line)
                    seg = line[p:end]
                    m = name_re.search(seg)
                    if not m:
                        continue
                    u = id_re.search(seg)
                    usr = u.group(1) if u else ""
                    actors[str(actor)] = {"usr": usr, "name": m.group(1)}
                    if usr:
                        users[usr] = m.group(1)

    _save(out_json, {"updated": int(time.time()), "actors": actors, "users": users})
    _save(state_path, {"offset": size, "actors": actors, "users": users})
    return actors, users

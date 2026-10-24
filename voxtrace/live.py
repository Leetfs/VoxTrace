"""Live voice extraction loop."""
import json
import os
import re
import time

from . import crypto, uspeak
from .opuslib import Decoder
from .wavio import WavWriter

WORLD_RE = re.compile(rb"wrld_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}[^\x00]{0,80}")

def write_json(path, obj):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


def load_names(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data.get("actors", {})
    except Exception:
        return {}


class Stream:
    def __init__(self, key, actor, usr, name, segment, outdir, cfg):
        self.key = key
        self.actor = actor
        self.usr = usr
        self.name = name or ""
        self.segment = segment
        base = usr if usr else "actor%d" % actor
        suffix = "" if segment <= 1 else "_s%d" % segment
        self.path = os.path.join(outdir, "player_%s%s.wav" % (base, suffix))
        self.wav = WavWriter(self.path)
        self.decoder = Decoder(cfg)
        self.last_index = None
        self.samples = 0
        self.packets = 0
        self.gaps = 0
        self.dups = 0
        self.last_time = 0.0

    def feed(self, index, packet):
        self.packets += 1
        self.last_time = time.time()
        if self.last_index is not None:
            delta = (index - self.last_index) & 0xFFFF
            if delta == 0:
                self.dups += 1
                return
            if 1 < delta <= 50:
                self.gaps += delta - 1
                self.wav.write_pcm(self.decoder.plc(delta - 1))
        pcm = self.decoder.decode(packet)
        if pcm:
            self.wav.write_pcm(pcm)
            self.samples += len(pcm)
            self.last_index = index

    def flush(self):
        self.wav.flush()

    def close(self):
        self.wav.close()
        try:
            self.decoder.close()
        except Exception:
            pass


def run(cfg, capture, key, outdir, once=False, stop_event=None, poll=0.5):
    """Tail ``capture`` and write one WAV per speaker.

    ``key`` is 32 raw bytes. Returns a stats dict when ``once`` is True.
    """
    os.makedirs(outdir, exist_ok=True)
    names_path = os.path.join(outdir, "names.json")
    stats_path = os.path.join(outdir, "live_stats.json")
    manifest_path = os.path.join(outdir, "session.json")

    streams = {}
    frags = {}
    stats = {"packets": 0, "voice": 0}
    pos = 0
    last_flush = time.time()
    world = ""
    segment = 1
    started = int(time.time())

    def stream_for(direction, actor):
        # own microphone goes to a separate track
        key_id = "self" if direction == "C" else "a%d" % actor
        st = streams.get(key_id)
        if st is not None:
            return st
        info = {} if direction == "C" else load_names(names_path).get(str(actor), {})
        st = Stream(key_id, actor, info.get("usr", ""), info.get("name", ""),
                    segment, outdir, cfg)
        streams[key_id] = st
        return st

    def flush_all():
        for st in streams.values():
            st.flush()

    def write_state():
        now = time.time()
        players = []
        for st in streams.values():
            players.append({
                "key": st.key,
                "actor": st.actor,
                "usr": st.usr,
                "name": st.name,
                "file": os.path.basename(st.path),
                "seconds": round(st.samples / 48000.0, 1),
                "packets": st.packets,
                "gaps": st.gaps,
                "dups": st.dups,
                "speaking": (now - st.last_time) < 1.5,
                "last": round(st.last_time, 1),
            })
        write_json(stats_path, {
            "updated": round(now, 1), "world": world, "segment": segment,
            "datagrams": stats["packets"], "voice": stats["voice"],
            "players": players,
        })
        write_json(manifest_path, {
            "capture": os.path.abspath(capture),
            "started": started,
            "updated": int(now),
            "world": world,
            "segment": segment,
            "players": len(players),
        })

    while True:
        try:
            if os.path.getsize(capture) < pos:
                pos = 0
        except OSError:
            pass
        with open(capture, "r", errors="ignore") as f:
            f.seek(pos)
            lines = f.readlines()
            pos = f.tell()
        for line in lines:
            result = crypto.decrypt_datagram(key, line)
            if not result:
                continue
            direction, plaintext = result
            stats["packets"] += 1
            if direction == "C" and b"wrld_" in plaintext:
                m = WORLD_RE.search(plaintext)
                if m:
                    new_world = m.group(0).decode("latin1", "ignore").split("\x00")[0]
                    if new_world != world and world:
                        for st in streams.values():
                            st.close()
                        streams.clear()
                        segment += 1
                    world = new_world
            for d, message in uspeak.iter_messages(plaintext, direction, frags):
                pos2 = message.find(b"\xf3\x04")
                if pos2 < 0:
                    continue
                event = uspeak.parse_event(message[pos2:])
                if not event:
                    continue
                code, sender, blobs = event
                if code != 1 or sender is None:
                    continue
                for pcode, data in blobs:
                    if pcode != 245 or len(data) <= 8:
                        continue
                    st = stream_for(d, sender)
                    for index, packet in uspeak.uspeak_packets(data):
                        st.feed(index, packet)
                        stats["voice"] += 1
        if time.time() - last_flush > 2:
            flush_all()
            write_state()
            last_flush = time.time()
        if stop_event is not None and stop_event.is_set():
            break
        if once:
            break
        time.sleep(poll)

    flush_all()
    write_state()
    for st in streams.values():
        st.close()
    stats["players"] = len(streams)
    return stats

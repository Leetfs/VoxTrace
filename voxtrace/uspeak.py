"""USpeak / Photon message parsing.

VRChat voice is Photon Event 1 in USpeak framing::

    [4 byte header][uint16 index][uint16 size][opus payload]...
"""
import struct


def read_varint(buf, i):
    value = 0
    shift = 0
    while i < len(buf):
        c = buf[i]
        value |= (c & 0x7F) << shift
        i += 1
        if not c & 0x80:
            break
        shift += 7
        if shift > 28:
            break
    return value, i


def parse_event(msg):
    """Parse ``F3 04 ...`` into (code, sender, [(param_code, bytes)])."""
    if len(msg) < 4 or msg[0] != 0xF3 or msg[1] != 0x04:
        return None
    code = msg[2]
    count = msg[3]
    i = 4
    sender = None
    blobs = []
    for _ in range(count):
        if i + 2 > len(msg):
            return None
        pcode, ptype = msg[i], msg[i + 1]
        i += 2
        if ptype == 0x43:
            length, i = read_varint(msg, i)
            blobs.append((pcode, msg[i:i + length]))
            i += length
        elif ptype == 0x0D:
            value = struct.unpack_from("<h", msg, i)[0]
            i += 2
            if pcode == 254:
                sender = value
        elif ptype == 0x0B:
            value = msg[i]
            i += 1
            if pcode == 254:
                sender = value
        elif ptype == 0x08:
            pass
        else:
            return None
    return code, sender, blobs


def uspeak_packets(payload):
    """Yield ``(index, opus_bytes)`` from a USpeak payload."""
    pos = 4
    while pos + 4 <= len(payload):
        index, size = struct.unpack_from("<HH", payload, pos)
        pos += 4
        if size == 0 or size > 1023 or pos + size > len(payload):
            return
        yield index, payload[pos:pos + size]
        pos += size


def iter_messages(plaintext, direction, frags):
    """Yield ``(direction, message)`` reassembling Photon fragments.

    ``frags`` is a dict owned by the caller and shared between datagrams.
    """
    if len(plaintext) < 5:
        return
    count = plaintext[0]
    off = 5
    for _ in range(count):
        if off + 12 > len(plaintext):
            break
        cmd_type = plaintext[off]
        length = int.from_bytes(plaintext[off + 4:off + 8], "big")
        payload = plaintext[off + 12:off + 12 + length]
        off += 12 + length
        if cmd_type == 8 and len(payload) >= 20:
            start, frag_count, frag_num, total, frag_off = struct.unpack_from(">IIIII", payload, 0)
            key = (direction, start)
            entry = frags.setdefault(key, {"n": frag_count, "parts": {}})
            entry["parts"][frag_num] = (frag_off, payload[20:])
            if len(entry["parts"]) == entry["n"]:
                data = bytearray(total)
                for _, (o, chunk) in sorted(entry["parts"].items()):
                    data[o:o + len(chunk)] = chunk
                del frags[key]
                yield direction, bytes(data)
        elif cmd_type != 8:
            yield direction, payload

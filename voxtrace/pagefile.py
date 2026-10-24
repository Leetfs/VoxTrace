"""Read a system file's extents from NTFS and scan them for the session key.

Requires Administrator privileges (raw volume access).  The algorithm was
validated on a 1.8 TB NTFS volume with a 3 GB pagefile.
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct

from Crypto.Cipher import AES

GENERIC_READ = 0x80000000
FILE_SHARE_READ = 0x1
FILE_SHARE_WRITE = 0x2
OPEN_EXISTING = 3

_k32 = ctypes.windll.kernel32
_k32.CreateFileW.restype = wt.HANDLE
_k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                             wt.DWORD, wt.DWORD, wt.HANDLE]
_k32.ReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                          ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
_k32.SetFilePointerEx.argtypes = [wt.HANDLE, ctypes.c_longlong,
                                  ctypes.POINTER(ctypes.c_longlong), wt.DWORD]


class Volume:
    def __init__(self, drive="C:"):
        path = "\\\\.\\" + drive.rstrip("\\")
        self.handle = _k32.CreateFileW(path, GENERIC_READ,
                                       FILE_SHARE_READ | FILE_SHARE_WRITE,
                                       None, OPEN_EXISTING, 0, None)
        if not self.handle or self.handle == wt.HANDLE(-1).value:
            raise OSError(ctypes.get_last_error(),
                          "cannot open %s (run as Administrator)" % path)
        self.read(0, 512)

    def read_into(self, buffer, offset, size):
        pos = ctypes.c_longlong(0)
        _k32.SetFilePointerEx(self.handle, ctypes.c_longlong(offset),
                              ctypes.byref(pos), 0)
        got = wt.DWORD(0)
        if not _k32.ReadFile(self.handle, buffer, size, ctypes.byref(got), None):
            raise OSError(ctypes.get_last_error(), "read failed at %d" % offset)
        return got.value

    def read(self, offset, size):
        pos = ctypes.c_longlong(0)
        _k32.SetFilePointerEx(self.handle, ctypes.c_longlong(offset),
                              ctypes.byref(pos), 0)
        buf = ctypes.create_string_buffer(size)
        got = wt.DWORD(0)
        if not _k32.ReadFile(self.handle, buf, size, ctypes.byref(got), None):
            raise OSError(ctypes.get_last_error(), "read failed at %d" % offset)
        return buf.raw[:got.value]

    def close(self):
        _k32.CloseHandle(self.handle)


def _boot(volume):
    data = volume.read(0, 512)
    bps = struct.unpack_from("<H", data, 0x0B)[0]
    spc = data[0x0D]
    total = struct.unpack_from("<Q", data, 0x28)[0]
    mft_lcn = struct.unpack_from("<Q", data, 0x30)[0]
    rec_code = struct.unpack_from("<b", data, 0x40)[0]
    rec_size = rec_code if rec_code > 0 else 1 << (-rec_code)
    return {"bps": bps, "cluster": bps * spc, "total": total,
            "mft_lcn": mft_lcn, "rec_size": rec_size}


def _attrs(record):
    off = struct.unpack_from("<H", record, 0x14)[0]
    while off + 16 <= len(record):
        atype, alen = struct.unpack_from("<II", record, off)
        if atype == 0xFFFFFFFF or alen == 0:
            break
        non_resident = record[off + 8]
        name_len = record[off + 9]
        name_off = struct.unpack_from("<H", record, off + 10)[0]
        name = record[off + name_off:off + name_off + name_len * 2].decode("utf-16-le", "ignore")
        if non_resident:
            run_off = struct.unpack_from("<H", record, off + 0x20)[0]
            size = struct.unpack_from("<Q", record, off + 0x30)[0]
            yield {"type": atype, "name": name, "non_resident": True,
                   "runs": record[off + run_off:off + alen], "size": size}
        else:
            vlen = struct.unpack_from("<I", record, off + 0x10)[0]
            voff = struct.unpack_from("<H", record, off + 0x14)[0]
            yield {"type": atype, "name": name, "non_resident": False,
                   "value": record[off + voff:off + voff + vlen]}
        off += alen


def _runlist(data):
    runs = []
    i = 0
    lcn = 0
    while i < len(data):
        header = data[i]
        i += 1
        if header == 0:
            break
        length_size = header & 0x0F
        offset_size = header >> 4
        length = int.from_bytes(data[i:i + length_size], "little")
        i += length_size
        delta = int.from_bytes(data[i:i + offset_size], "little", signed=True)
        i += offset_size
        lcn += delta
        runs.append((lcn, length))
    return runs


class Ntfs:
    def __init__(self, volume):
        self.volume = volume
        self.boot = _boot(volume)
        self.rec_size = self.boot["rec_size"]
        self.mft_runs = None

    def _mft(self):
        rec = self.volume.read(self.boot["mft_lcn"] * self.boot["cluster"], self.rec_size)
        for attr in _attrs(rec):
            if attr["type"] == 0x80 and attr["non_resident"] and attr["name"] == "":
                return _runlist(attr["runs"])
        raise RuntimeError("MFT data attribute not found")

    def record(self, number):
        if self.mft_runs is None:
            self.mft_runs = self._mft()
        byte_off = number * self.rec_size
        vcn = byte_off // self.boot["cluster"]
        within = byte_off % self.boot["cluster"]
        current = 0
        for lcn, count in self.mft_runs:
            if current <= vcn < current + count:
                return self.volume.read((lcn + vcn - current) * self.boot["cluster"] + within,
                                        self.rec_size)
            current += count
        raise RuntimeError("MFT record %d outside runlist" % number)

    def lookup_root(self, name):
        record = self.record(5)
        for attr in _attrs(record):
            if attr["type"] != 0x90:
                continue
            value = attr["value"]
            entries = struct.unpack_from("<I", value, 0x10)[0]
            pos = 0x10 + entries
            while pos + 16 <= len(value):
                ref = struct.unpack_from("<Q", value, pos)[0]
                elen = struct.unpack_from("<H", value, pos + 8)[0]
                klen = struct.unpack_from("<H", value, pos + 10)[0]
                flags = struct.unpack_from("<H", value, pos + 12)[0]
                key = value[pos + 16:pos + 16 + klen]
                if len(key) >= 0x42:
                    nlen = key[0x40]
                    fname = key[0x42:0x42 + nlen * 2].decode("utf-16-le", "ignore")
                    if fname.lower() == name.lower():
                        return ref & 0xFFFFFFFFFFFF
                if flags & 0x02:
                    break
                pos += elen
        return None

    def data_runs(self, number):
        record = self.record(number)
        result = []
        for attr in _attrs(record):
            if attr["type"] == 0x80 and attr["non_resident"] and attr["name"] == "":
                result.append((attr["size"], _runlist(attr["runs"])))
        return result


def locate(drive, filename):
    """Return (boot, record_number, [(size, [(lcn, count), ...])])."""
    volume = Volume(drive)
    try:
        ntfs = Ntfs(volume)
        number = ntfs.lookup_root(filename)
        if number is None:
            raise RuntimeError("%s not found in root directory" % filename)
        runs = ntfs.data_runs(number)
        return ntfs.boot, number, runs
    finally:
        volume.close()


def iter_ciphertexts(capture, limit=8, tail=True):
    """Collect (aad, nonce, ciphertext, tag) tuples from a capture file.

    With ``tail=True`` only the last 4 MiB are read, so reconnects that rotate
    the key still verify against recent traffic instead of stale packets.
    """
    out = []
    start = 0
    if tail:
        import os as _os
        size = _os.path.getsize(capture)
        start = max(0, size - 4 * 1024 * 1024)
    with open(capture, "r", errors="ignore") as f:
        if start:
            f.seek(start)
            f.readline()
        for line in f:
            parts = line.strip().split(",")
            if len(parts) != 3:
                continue
            try:
                pkt = bytes.fromhex(parts[2])
            except ValueError:
                continue
            if len(pkt) < 40 or pkt[2] != 1:
                continue
            header, blob = pkt[:7], pkt[7:]
            if len(blob) < 28:
                continue
            nonce, ct, tag = blob[:12], blob[12:-16], blob[-16:]
            out.append((header + nonce, nonce, ct, tag))
            if len(out) > limit:
                out.pop(0)
    return out


def _verify(key, ciphertexts):
    for aad, nonce, ct, tag in ciphertexts:
        try:
            gcm = AES.new(key, AES.MODE_GCM, nonce=nonce)
            gcm.update(aad)
            gcm.decrypt_and_verify(ct, tag)
            return True
        except Exception:
            pass
    return False


def scan_buffer(data, ciphertexts):
    """Return (offset, key_hex) for the first matching key in ``data``."""
    pattern = b"\x20\x00\x00\x00\x00\x00\x00\x00"
    start = 0
    while True:
        i = data.find(pattern, start)
        if i < 0 or i + 40 > len(data):
            return None
        start = i + 1
        key = data[i + 8:i + 40]
        if len(set(key)) < 16:
            continue
        if _verify(key, ciphertexts):
            return (i + 8, key.hex())


def _scan_range(args):
    drive, base, length, overflow, ciphertexts = args
    volume = Volume(drive)
    try:
        buf = ctypes.create_string_buffer(min(CHUNK, length) + overflow)
        pos = 0
        while pos < length:
            n = min(CHUNK, length - pos)
            size = min(n + overflow, length + overflow - pos)
            try:
                got = volume.read_into(buf, base + pos, size)
            except OSError:
                got = volume.read_into(buf, base + pos, n)
            data = buf.raw[:got]
            hit = scan_buffer(data, ciphertexts)
            if hit:
                return (base + pos + hit[0], hit[1])
            pos += n
        return None
    finally:
        volume.close()


CHUNK = 128 * 1024 * 1024
OVERFLOW = 64


def _verify_candidates(out_path, ciphertexts, log):
    seen = set()
    candidates = 0
    if not os.path.isfile(out_path):
        return None
    with open(out_path, "r", errors="ignore") as f:
        for line in f:
            parts = line.split()
            if len(parts) != 2 or len(parts[1]) != 64:
                continue
            if parts[1] in seen:
                continue
            seen.add(parts[1])
            candidates += 1
            key = bytes.fromhex(parts[1])
            if _verify(key, ciphertexts):
                log("KEY FOUND at disk offset %s: %s" % (parts[0], parts[1]))
                return parts[1]
    log("candidate keys=%d, no match" % candidates)
    return None


def _fast_scan(drive, scanner, ciphertexts, log, threads=8):
    import os, subprocess, tempfile
    boot, number, run_sets = locate(drive, "pagefile.sys")
    jobs = []
    for size, runs in run_sets:
        for lcn, count in runs:
            base = lcn * boot["cluster"]
            total = count * boot["cluster"]
            done = 0
            while done < total:
                jobs.append((base + done, min(256 * 1024 * 1024, total - done) + 64))
                done += 256 * 1024 * 1024
    tmp = tempfile.mkdtemp(prefix="voxtrace_scan_")
    jobs_path = os.path.join(tmp, "jobs.txt")
    out_path = os.path.join(tmp, "candidates.txt")
    try:
        with open(jobs_path, "w") as f:
            for base, length in jobs:
                f.write("%d %d\n" % (base, length))
        log("fast scan: %d segments, %d jobs" % (len(run_sets), len(jobs)))
        proc = subprocess.run([scanner, "--drive", drive, "--jobs", jobs_path,
                               "--out", out_path, "--threads", str(threads)],
                              timeout=180, check=False,
                              capture_output=True, text=True)
        log("fast scan exit=%d" % proc.returncode)
        if proc.stdout.strip():
            log("fast stdout: " + proc.stdout.strip()[:400])
        if proc.stderr.strip():
            log("fast stderr: " + proc.stderr.strip()[:400])
        return _verify_candidates(out_path, ciphertexts, log)
    finally:
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)


def scan(drive, filename, ciphertexts, chunk=CHUNK, workers=4, log=print, scanner=None,
         fast_threads=8, allow_python=True):
    if scanner and os.path.isfile(scanner):
        found = _fast_scan(drive, scanner, ciphertexts, log, fast_threads)
        if found:
            return found
    else:
        log("FastScan.exe not found; %s" %
            ("skipping round" if not allow_python else "falling back to Python scan"))
    if not allow_python:
        log("skipping python fallback for this round")
        return None
    return _scan_python(drive, filename, ciphertexts, chunk, workers, log)


def _scan_python(drive, filename, ciphertexts, chunk=CHUNK, workers=4, log=print):
    """Scan the file's extents for a 32 byte key matching the ciphertexts."""
    boot, number, run_sets = locate(drive, filename)
    log("boot=%s" % boot)
    log("%s: MFT record %d, %d run set(s)" % (filename, number, len(run_sets)))
    segment = 256 * 1024 * 1024
    jobs = []
    for size, runs in run_sets:
        for lcn, count in runs:
            base = lcn * boot["cluster"]
            total = count * boot["cluster"]
            done = 0
            while done < total:
                jobs.append((drive, base + done, min(segment, total - done), OVERFLOW, ciphertexts))
                done += segment
    log("scan jobs=%d workers=%d" % (len(jobs), workers))
    if workers <= 1 or len(jobs) <= 1:
        for job in jobs:
            found = _scan_range(job)
            if found:
                log("KEY FOUND at disk offset %d: %s" % found)
                return found[1]
        log("no key found")
        return None
    import multiprocessing
    ctx = multiprocessing.get_context("spawn")
    try:
        step = max(1, len(jobs) // 4)
        done = 0
        with ctx.Pool(processes=min(workers, len(jobs))) as pool:
            for result in pool.imap_unordered(_scan_range, jobs, chunksize=1):
                done += 1
                if result:
                    log("KEY FOUND at disk offset %d: %s" % result)
                    pool.terminate()
                    return result[1]
                if done % step == 0 and done < len(jobs):
                    log("scan progress %d/%d segments" % (done, len(jobs)))
    except Exception as exc:
        log("parallel scan failed (%s), falling back to serial" % exc)
        for job in jobs:
            found = _scan_range(job)
            if found:
                log("KEY FOUND at disk offset %d: %s" % found)
                return found[1]
    log("no key found")
    return None


def dry_scan(drive, filename="pagefile.sys", chunk=256 * 1024 * 1024, log=print):
    """Read the whole file and count pattern occurrences without verifying."""
    import time
    boot, number, run_sets = locate(drive, filename)
    pattern = b"\x20\x00\x00\x00\x00\x00\x00\x00"
    volume = Volume(drive)
    total_bytes = 0
    hits = 0
    t0 = time.time()
    try:
        for size, runs in run_sets:
            for lcn, count in runs:
                base = lcn * boot["cluster"]
                length = count * boot["cluster"]
                pos = 0
                while pos < length:
                    n = min(chunk, length - pos)
                    data = volume.read(base + pos, n)
                    hits += data.count(pattern)
                    total_bytes += len(data)
                    pos += n
    finally:
        volume.close()
    dt = time.time() - t0
    log("scanned %.2f GB in %.2fs (%.0f MB/s), pattern hits=%d"
        % (total_bytes / 1073741824.0, dt, total_bytes / 1048576.0 / dt, hits))

"""Raw volume read benchmark (read-only).

Run as Administrator:
    python -m voxtrace bench --drive C: --mb 512

Compares buffered and FILE_FLAG_NO_BUFFERING reads at several chunk sizes.
No files are written; only the first extent of pagefile.sys is read.
"""
import ctypes
import ctypes.wintypes as wt
import time

from . import pagefile

GENERIC_READ = 0x80000000
FILE_SHARE_READ = 0x1
FILE_SHARE_WRITE = 0x2
OPEN_EXISTING = 3
FILE_FLAG_NO_BUFFERING = 0x20000000
MEM_COMMIT = 0x1000
MEM_RESERVE = 0x2000
PAGE_READWRITE = 0x04
MEM_RELEASE = 0x8000

_k32 = ctypes.windll.kernel32
_k32.CreateFileW.restype = wt.HANDLE
_k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                             wt.DWORD, wt.DWORD, wt.HANDLE]
_k32.ReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                          ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
_k32.SetFilePointerEx.argtypes = [wt.HANDLE, ctypes.c_longlong,
                                  ctypes.POINTER(ctypes.c_longlong), wt.DWORD]
_k32.VirtualAlloc.restype = ctypes.c_void_p
_k32.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]
_k32.VirtualFree.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wt.DWORD]


class AlignedBuffer:
    def __init__(self, size):
        self.size = size
        self.ptr = _k32.VirtualAlloc(None, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
        if not self.ptr:
            raise MemoryError("VirtualAlloc failed")

    def data(self, n):
        return ctypes.string_at(self.ptr, n)

    def free(self):
        if self.ptr:
            _k32.VirtualFree(self.ptr, 0, MEM_RELEASE)
            self.ptr = None


class FastVolume:
    def __init__(self, drive="C:", unbuffered=False):
        path = "\\\\.\\" + drive.rstrip("\\")
        flags = FILE_FLAG_NO_BUFFERING if unbuffered else 0
        self.unbuffered = unbuffered
        self.handle = _k32.CreateFileW(path, GENERIC_READ,
                                       FILE_SHARE_READ | FILE_SHARE_WRITE,
                                       None, OPEN_EXISTING, flags, None)
        if not self.handle or self.handle == wt.HANDLE(-1).value:
            raise OSError(ctypes.get_last_error(), "cannot open " + path)

    def read_at(self, buf, offset, size):
        pos = ctypes.c_longlong(0)
        _k32.SetFilePointerEx(self.handle, ctypes.c_longlong(offset),
                              ctypes.byref(pos), 0)
        got = wt.DWORD(0)
        if not _k32.ReadFile(self.handle, ctypes.c_void_p(buf.ptr), size,
                             ctypes.byref(got), None):
            raise OSError(ctypes.get_last_error(), "read failed at %d" % offset)
        return got.value

    def close(self):
        if self.handle:
            _k32.CloseHandle(self.handle)
            self.handle = None


def bench(drive="C:", megabytes=512, log=print):
    boot, number, runs = pagefile.locate(drive, "pagefile.sys")
    size, runlist = runs[0]
    base = runlist[0][0] * boot["cluster"]
    total = min(megabytes * 1024 * 1024, size)
    log("volume cluster=%d, first extent LCN=%d, test size=%d MB"
        % (boot["cluster"], runlist[0][0], total // 1048576))
    for chunk_mb in (4, 16, 64, 128):
        chunk = chunk_mb * 1024 * 1024
        if chunk > total:
            continue
        for unbuffered in (False, True):
            label = ("no-buffer " if unbuffered else "buffered  ") + ("%d MB" % chunk_mb)
            try:
                vol = FastVolume(drive, unbuffered)
                buf = AlignedBuffer(chunk)
            except Exception as exc:
                log("%s: unavailable (%s)" % (label, exc))
                continue
            try:
                done = 0
                t0 = time.time()
                while done < total:
                    n = min(chunk, total - done)
                    n -= n % 512
                    if n <= 0:
                        break
                    got = vol.read_at(buf, base + done, n)
                    done += got
                dt = time.time() - t0
                log("%s: %.1f MB/s (%.2fs)" % (label, done / 1048576.0 / dt, dt))
            except Exception as exc:
                log("%s: failed (%s)" % (label, exc))
            finally:
                vol.close()
                buf.free()

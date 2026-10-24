"""Wait for dirty pages to be written back to the pagefile.

EmptyWorkingSet moves private dirty pages to the system modified page list;
the memory manager writes them to pagefile.sys asynchronously.  Instead of a
fixed sleep, poll SystemMemoryListInformation until the list drains.
"""
import ctypes
import time

_ntdll = ctypes.WinDLL("ntdll")
_ntdll.NtQuerySystemInformation.restype = ctypes.c_ulong
_ntdll.NtSetSystemInformation.restype = ctypes.c_ulong
_ntdll.NtSetSystemInformation.argtypes = [ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong]
_ntdll.NtQuerySystemInformation.argtypes = [ctypes.c_ulong, ctypes.c_void_p,
                                            ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong)]

SYSTEM_MEMORY_LIST_INFORMATION = 80


class _MemoryList(ctypes.Structure):
    _fields_ = [
        ("ZeroPageCount", ctypes.c_size_t),
        ("FreePageCount", ctypes.c_size_t),
        ("ModifiedPageCount", ctypes.c_size_t),
        ("ModifiedNoWritePageCount", ctypes.c_size_t),
        ("BadPageCount", ctypes.c_size_t),
        ("PageCountByPriority", ctypes.c_size_t * 8),
        ("RepurposedPagesByPriority", ctypes.c_size_t * 8),
        ("ModifiedPageCountPageFile", ctypes.c_size_t),
    ]


def modified_pages():
    """Return the number of pages currently on the modified page list."""
    info = _MemoryList()
    returned = ctypes.c_ulong(0)
    status = _ntdll.NtQuerySystemInformation(SYSTEM_MEMORY_LIST_INFORMATION,
                                             ctypes.byref(info),
                                             ctypes.sizeof(info),
                                             ctypes.byref(returned))
    if status != 0:
        raise OSError("NtQuerySystemInformation failed: 0x%08x" % (status & 0xFFFFFFFF))
    return info.ModifiedPageCount


MEMORY_FLUSH_MODIFIED_LIST = 3


def flush_modified(log=print):
    """Ask the kernel to write the modified page list to disk (RAMMap trick)."""
    command = ctypes.c_ulong(MEMORY_FLUSH_MODIFIED_LIST)
    status = _ntdll.NtSetSystemInformation(SYSTEM_MEMORY_LIST_INFORMATION,
                                           ctypes.byref(command),
                                           ctypes.sizeof(command))
    if status == 0:
        return True
    log("flush modified list failed: 0x%08x" % (status & 0xFFFFFFFF))
    return False


def flush_until_drained(baseline=None, timeout=10.0, log=print):
    """Repeatedly flush the modified list until it stays near baseline.

    EmptyWorkingSet queues pages asynchronously, so one flush can race with it.
    """
    try:
        base = modified_pages() if baseline is None else baseline
    except Exception:
        time.sleep(2.0)
        return -1
    t0 = time.time()
    while True:
        flush_modified(log=log)
        time.sleep(0.15)
        try:
            current = modified_pages()
        except Exception:
            return -1
        if current <= base + 8192:
            elapsed = time.time() - t0
            log("drain: %d pages left after %.2fs" % (current, elapsed))
            return elapsed
        if time.time() - t0 >= timeout:
            log("drain: timeout (baseline=%d now=%d)" % (base, current))
            return time.time() - t0
        time.sleep(0.15)

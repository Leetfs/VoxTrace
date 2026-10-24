"""Session key acquisition.

Steps: trim the game's working set (PROCESS_SET_QUOTA only, no memory read),
wait for dirty pages to reach pagefile.sys, then scan the pagefile extents.
"""
import ctypes
import ctypes.wintypes as wt
import os
import time

from . import drain, pagefile

PROCESS_SET_QUOTA = 0x0100
PROCESS_QUERY_INFORMATION = 0x0400

_k32 = ctypes.windll.kernel32
_psapi = ctypes.windll.psapi
_k32.OpenProcess.restype = wt.HANDLE
_k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
_psapi.EmptyWorkingSet.argtypes = [wt.HANDLE]


class _PMC(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]


def find_pid(exe_name="VRChat.exe"):
    snapshot = _k32.CreateToolhelp32Snapshot(0x2, 0)

    class Entry(ctypes.Structure):
        _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wt.DWORD),
                    ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                    ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                    ("szExeFile", wt.WCHAR * 260)]
    entry = Entry()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        ok = _k32.Process32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.lower() == exe_name.lower():
                return entry.th32ProcessID
            ok = _k32.Process32NextW(snapshot, ctypes.byref(entry))
        return None
    finally:
        _k32.CloseHandle(snapshot)


def working_set_mb(handle):
    pmc = _PMC()
    pmc.cb = ctypes.sizeof(pmc)
    _psapi.GetProcessMemoryInfo(handle, ctypes.byref(pmc), pmc.cb)
    return pmc.WorkingSetSize / 1048576.0


def trim(pid):
    handle = _k32.OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, False, pid)
    if not handle:
        raise OSError(ctypes.get_last_error(), "OpenProcess failed (run as Administrator?)")
    try:
        before = working_set_mb(handle)
        ok = _psapi.EmptyWorkingSet(handle)
        after = working_set_mb(handle)
        return bool(ok), before, after
    finally:
        _k32.CloseHandle(handle)


def extract(cfg, capture, drive=None, log=print, settle=None):
    drive = drive or cfg.get("pagefile_drive", "C:")
    if settle is None:
        settle = float(cfg.get("settle_seconds", 2.0))
    ciphertexts = pagefile.iter_ciphertexts(capture)
    if not ciphertexts:
        log("capture has no encrypted datagrams yet")
        return None
    pid = find_pid("VRChat.exe")
    if not pid:
        log("VRChat.exe is not running")
        return None
    scanner = cfg.get("fastscan_exe") or "bin/FastScan.exe"
    if not os.path.isabs(scanner):
        scanner = os.path.join(cfg["root"], scanner)
    for round_no in range(1, 4):
        try:
            baseline = drain.modified_pages()
        except Exception:
            baseline = None
        for trim_no in (1, 2):
            try:
                ok, before, after = trim(pid)
            except OSError as exc:
                log("trim failed: %s (run as Administrator?)" % exc)
                return None
            log("trim round=%d pass=%d pid=%d ok=%s working_set %.0f MB -> %.0f MB"
                % (round_no, trim_no, pid, ok, before, after))
            if baseline is None:
                time.sleep(settle)
            else:
                drain.flush_until_drained(baseline,
                                          timeout=float(cfg.get("drain_timeout", 10.0)),
                                          log=log)
            time.sleep(0.2)
        found = pagefile.scan(drive, "pagefile.sys", ciphertexts,
                              workers=int(cfg.get("scan_workers", 4)), log=log,
                              scanner=scanner,
                              fast_threads=int(cfg.get("fastscan_threads", 8)),
                              allow_python=(round_no == 3))
        if found:
            return found
        log("round %d missed" % round_no)
        time.sleep(0.4)
    return None

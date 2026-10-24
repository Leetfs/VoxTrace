"""Configuration loading.

The project root is the directory that contains ``config.json``.  The GUI and
all command line entry points resolve it the same way: explicit ``VOXTRACE_ROOT``
environment variable, then upward search from the current directory.
"""
import json
import os

DEFAULTS = {
    "vrchat_dir": "",
    "opus_dll": "",
    "relay_exe": "bin/VrcWintunPoc.exe",
    "wintun_dll": "bin/wintun.dll",
    "python": "",
    "captures_dir": "captures",
    "eventparse_exe": "bin/eventparse/eventparse.exe",
    "fastscan_exe": "bin/FastScan.exe",
    "dotnet_root": "",
    "pagefile_drive": "C:",
    "scan_workers": 4,
    "fastscan_threads": 4,
    "settle_seconds": 2.0,
    "drain_timeout": 10.0,
    "gap_fill": "silence",
    "relay_prefix": "82.117.224.0/24",
    "relay_port": 27002,
}


def find_root(start=None):
    env = os.environ.get("VOXTRACE_ROOT") or os.environ.get("VOICEKIT_ROOT")
    if env and os.path.isfile(os.path.join(env, "config.json")):
        return os.path.abspath(env)
    p = os.path.abspath(start or os.getcwd())
    while True:
        if os.path.isfile(os.path.join(p, "config.json")):
            return p
        parent = os.path.dirname(p)
        if parent == p:
            break
        p = parent
    return os.path.abspath(start or os.getcwd())


def load(root=None):
    root = root or find_root()
    path = os.path.join(root, "config.json")
    cfg = dict(DEFAULTS)
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8-sig") as f:
            cfg.update(json.load(f))
    cfg["root"] = root
    cfg["_path"] = path
    return cfg


def resolve(cfg, value):
    """Resolve a config path relative to the project root."""
    if not value:
        return ""
    if os.path.isabs(value):
        return value
    return os.path.abspath(os.path.join(cfg["root"], value))


def captures_dir(cfg):
    d = resolve(cfg, cfg.get("captures_dir") or "captures")
    os.makedirs(d, exist_ok=True)
    return d


def find_python(cfg):
    if cfg.get("python"):
        p = resolve(cfg, cfg["python"])
        if os.path.isfile(p):
            return p
    cands = [
        r"C:\Python313\python.exe",
        os.path.expandvars(r"%LOCALAPPDATA%\Programs\Python\Python313\python.exe"),
        os.path.expandvars(r"%LOCALAPPDATA%\Programs\Python\Python312\python.exe"),
    ]
    for p in cands:
        if os.path.isfile(p):
            return p
    import shutil
    return shutil.which("python") or "python"

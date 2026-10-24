"""libopus loading and decoding helpers.

The official VRChat client ships ``opus.dll``; VoxTrace loads the library from
``config.json`` (``opus_dll``), from ``vrchat_dir`` or from the system search
path.  No Opus binary is bundled with this project.
"""
import ctypes
import os

_opus = None


def find_library(cfg):
    candidates = []
    if cfg.get("opus_dll"):
        candidates.append(cfg["opus_dll"])
    if cfg.get("vrchat_dir"):
        candidates.append(os.path.join(cfg["vrchat_dir"], "VRChat_Data", "Plugins",
                                       "x86_64", "opus.dll"))
        candidates.append(os.path.join(cfg["vrchat_dir"], "VRChat_Data", "Plugins",
                                       "x86", "opus.dll"))
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    import ctypes.util
    name = ctypes.util.find_library("opus")
    if name:
        return name
    raise RuntimeError("opus library not found; set 'opus_dll' or 'vrchat_dir' in config.json")


def load(cfg):
    global _opus
    if _opus is not None:
        return _opus
    path = find_library(cfg)
    lib = ctypes.CDLL(path)
    lib.opus_decoder_create.restype = ctypes.c_void_p
    lib.opus_decoder_create.argtypes = [ctypes.c_int, ctypes.c_int,
                                        ctypes.POINTER(ctypes.c_int)]
    lib.opus_decoder_destroy.argtypes = [ctypes.c_void_p]
    lib.opus_decode.restype = ctypes.c_int
    lib.opus_decode.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int,
                                ctypes.POINTER(ctypes.c_int16), ctypes.c_int, ctypes.c_int]
    _opus = lib
    return lib


class Decoder:
    """48 kHz stereo Opus decoder producing mono samples."""

    def __init__(self, cfg):
        self.lib = load(cfg)
        self.handle = self.lib.opus_decoder_create(48000, 2, None)
        if not self.handle:
            raise RuntimeError("opus_decoder_create failed")

    def decode(self, packet):
        buf = (ctypes.c_int16 * (5760 * 2))()
        r = self.lib.opus_decode(self.handle, packet, len(packet), buf, 5760, 0)
        if r <= 0:
            return None
        s = buf[:r * 2]
        return [(s[i] + s[i + 1]) // 2 for i in range(0, len(s), 2)]

    def close(self):
        if self.handle:
            try:
                self.lib.opus_decoder_destroy(self.handle)
            except Exception:
                pass
            self.handle = None

    def plc(self, frames):
        buf = (ctypes.c_int16 * (5760 * 2))()
        out = []
        for _ in range(frames):
            r = self.lib.opus_decode(self.handle, None, 0, buf, 960, 0)
            if r > 0:
                s = buf[:r * 2]
                out.extend([(s[i] + s[i + 1]) // 2 for i in range(0, len(s), 2)])
            else:
                out.extend([0] * 960)
        return out

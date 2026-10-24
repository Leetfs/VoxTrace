"""Incremental mono 16-bit 48 kHz WAV writer."""
import struct


class WavWriter:
    def __init__(self, path):
        self.path = path
        self.f = open(path, "wb")
        self.f.write(b"RIFF" + b"\x00" * 4 + b"WAVE")
        self.f.write(b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, 48000, 96000, 2, 16))
        self.f.write(b"data" + b"\x00" * 4)
        self.data_len = 0
        self.f.flush()

    def write_pcm(self, samples):
        if not samples:
            return
        self.f.write(struct.pack("<%dh" % len(samples), *samples))
        self.data_len += len(samples) * 2

    def flush(self):
        try:
            self.f.seek(4)
            self.f.write(struct.pack("<I", 36 + self.data_len))
            self.f.seek(40)
            self.f.write(struct.pack("<I", self.data_len))
            self.f.seek(0, 2)
            self.f.flush()
        except Exception:
            pass

    def close(self):
        self.flush()
        try:
            self.f.close()
        except Exception:
            pass

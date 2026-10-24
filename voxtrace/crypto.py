"""Photon v5 datagram decryption (AES-256-GCM)."""
import binascii
import os

from Crypto.Cipher import AES


def load_key(path):
    """Read a 64 hex character key from a file."""
    with open(path, "r", encoding="utf-8") as f:
        raw = f.read().strip()
    if len(raw) != 64:
        raise ValueError("key file must contain 64 hex characters")
    return bytes.fromhex(raw)


def load_key_hex(text):
    text = text.strip()
    if len(text) != 64:
        raise ValueError("key must be 64 hex characters")
    return bytes.fromhex(text)


def decrypt_datagram(key, line):
    """Decrypt one capture line: ``<direction>,<type>,<hex>``.

    Returns ``(direction, plaintext)`` or ``None``.
    """
    parts = line.strip().split(",")
    if len(parts) != 3:
        return None
    try:
        pkt = bytes.fromhex(parts[2])
    except (ValueError, binascii.Error):
        return None
    if len(pkt) < 40 or pkt[2] != 1:
        return None
    header, blob = pkt[:7], pkt[7:]
    if len(blob) < 28:
        return None
    nonce, ciphertext, tag = blob[:12], blob[12:-16], blob[-16:]
    try:
        gcm = AES.new(key, AES.MODE_GCM, nonce=nonce)
        gcm.update(header + nonce)
        return parts[0], gcm.decrypt_and_verify(ciphertext, tag)
    except Exception:
        return None

"""Attach to UU and log route prefixes as they are installed.

Requires: pip install frida
Run as Administrator if UU runs elevated:
    python scripts/uu_routes_frida.py uu.exe > uu_routes.jsonl
"""

import json
import sys

import frida


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else "uu.exe"
    try:
        pid = int(target)
    except ValueError:
        pid = target

    session = frida.attach(pid)
    with open("scripts/uu_routes_hook.js", "r", encoding="utf-8") as f:
        source = f.read()

    script = session.create_script(source)

    def on_message(message, data):
        payload = message.get("payload")
        if payload and payload.get("ready"):
            print(json.dumps({"ready": True}), flush=True)
            return
        if payload:
            print(json.dumps(payload, ensure_ascii=False), flush=True)
        elif message.get("type") == "error":
            print(json.dumps(message), file=sys.stderr, flush=True)

    script.on("message", on_message)
    script.load()
    print(json.dumps({"attached": str(pid)}), flush=True)
    try:
        sys.stdin.read()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()

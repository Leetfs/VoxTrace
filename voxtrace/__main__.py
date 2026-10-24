"""VoxTrace command line interface.

Usage:
  python -m voxtrace live  --capture captures/voice.txt --key <hex|file> --out captures/live
  python -m voxtrace names --capture captures/voice.txt --key <hex> --out captures/live/names.json
  python -m voxtrace key   --capture captures/voice.txt
  python -m voxtrace locate --file pagefile.sys --drive C:
"""
import argparse
import os
import sys

from . import __version__, config, crypto, fastread, keytool, live, names, pagefile


def _key(value):
    if os.path.isfile(value):
        return crypto.load_key(value)
    return crypto.load_key_hex(value)


def main(argv=None):
    parser = argparse.ArgumentParser(prog="voxtrace", description="VRChat voice capture toolkit")
    parser.add_argument("--version", action="version", version=__version__)
    sub = parser.add_subparsers(dest="command", required=True)

    p_live = sub.add_parser("live", help="decode voice from a relay capture")
    p_live.add_argument("--capture", required=True)
    p_live.add_argument("--key", required=True, help="32-byte key as hex or a file containing it")
    p_live.add_argument("--out", required=True)
    p_live.add_argument("--once", action="store_true", help="process existing data and exit")

    p_names = sub.add_parser("names", help="extract actor -> displayName mapping")
    p_names.add_argument("--capture", required=True)
    p_names.add_argument("--key", required=True)
    p_names.add_argument("--out", required=True)

    p_key = sub.add_parser("key", help="trim the game and scan pagefile for the session key")
    p_key.add_argument("--capture", required=True)
    p_key.add_argument("--drive", default=None)

    p_bench = sub.add_parser("bench", help="measure raw volume read speed (read-only)")
    p_bench.add_argument("--drive", default="C:")
    p_bench.add_argument("--mb", type=int, default=512)

    p_dry = sub.add_parser("dry", help="count key-pattern hits in pagefile (read-only)")
    p_dry.add_argument("--drive", default="C:")
    p_dry.add_argument("--file", default="pagefile.sys")

    p_locate = sub.add_parser("locate", help="print NTFS extents of a system file")
    p_locate.add_argument("--file", default="pagefile.sys")
    p_locate.add_argument("--drive", default="C:")

    args = parser.parse_args(argv)
    cfg = config.load()

    if args.command == "live":
        stats = live.run(cfg, args.capture, _key(args.key), args.out, once=args.once)
        print("packets=%d voice=%d players=%d" % (stats["packets"], stats["voice"], stats["players"]))
        return 0

    if args.command == "names":
        actors, users = names.extract(cfg, args.capture, _key(args.key), args.out)
        print("actors=%d users=%d" % (len(actors), len(users)))
        return 0

    if args.command == "key":
        found = keytool.extract(cfg, args.capture, args.drive)
        if found:
            print("key:", found)
            return 0
        return 1

    if args.command == "bench":
        fastread.bench(args.drive, args.mb)
        return 0

    if args.command == "dry":
        pagefile.dry_scan(args.drive, args.file)
        return 0

    if args.command == "locate":
        boot, record, runs = pagefile.locate(args.drive, args.file)
        print("record=%d boot=%s" % (record, boot))
        for size, runlist in runs:
            print("size=%d extents=%d" % (size, len(runlist)))
            for lcn, count in runlist[:20]:
                print("  lcn=%d clusters=%d" % (lcn, count))
        return 0

    return 2


if __name__ == "__main__":
    sys.exit(main())

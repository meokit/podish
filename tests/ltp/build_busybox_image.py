#!/usr/bin/env python3
"""Build a reusable guest rootfs tar with a static i386 BusyBox.

The LTP runner embeds the same BusyBox layout into every per-run image. This
script produces a standalone base image for manual inspection or for other
guests that need a shell and common command-line tools.

Examples:
  python tests/ltp/build_busybox_image.py
  python tests/ltp/build_busybox_image.py --import --reference localhost/ltp-base:latest
"""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

import run

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--busybox", type=Path,
                        default=REPO / "artifacts/ltp-busybox/busybox",
                        help="Pinned static i386 BusyBox (downloaded if missing)")
    parser.add_argument("--applets", type=Path, default=run.APPLETS_FILE,
                        help="Checked-in BusyBox applet list")
    parser.add_argument("--out", type=Path,
                        default=REPO / "artifacts/ltp-busybox/rootfs.tar",
                        help="Output rootfs tar")
    parser.add_argument("--import", dest="do_import", action="store_true",
                        help="Import the tar with Podish.Cli")
    parser.add_argument("--cli", type=Path,
                        default=REPO / "Podish.Cli/bin/Debug/net11.0/Podish.Cli.dll",
                        help="Podish.Cli.dll used by --import")
    parser.add_argument("--reference", default="localhost/podish-ltp-base:latest",
                        help="Image reference used by --import")
    args = parser.parse_args()

    busybox = run.ensure_busybox(args.busybox.resolve())
    applets = run.read_applets(args.applets)
    args.out.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="podish-busybox-image-") as directory:
        root = Path(directory) / "rootfs"
        for path in ("bin", "sbin", "usr/bin", "usr/sbin", "etc", "tmp", "dev",
                     "proc", "run", "root", "home", "mnt", "media", "sys", "var",
                     "opt/ltp/testcases/bin"):
            (root / path).mkdir(parents=True, exist_ok=True)
        run.write_guest_identity_files(root)
        shutil.copy2(busybox, root / "bin/busybox")
        run.build_image_tar(root, args.out, {"bin/busybox"},
                            run.busybox_symlinks(applets))

    digest = hashlib.sha256(args.out.read_bytes()).hexdigest()
    print(f"Built {args.out} ({args.out.stat().st_size} bytes, sha256 {digest})")
    print(f"BusyBox applets: {len(applets)} in /bin, "
          f"{sum(name in run.SBIN_APPLETS for name in applets)} also in /sbin")

    if args.do_import:
        cli = args.cli.resolve()
        if not cli.is_file():
            raise SystemExit(f"Podish.Cli not found: {cli}")
        env = os.environ.copy()
        env.setdefault("DOTNET_ROLL_FORWARD", "LatestMajor")
        subprocess.run(["dotnet", str(cli), "import", str(args.out), args.reference],
                       check=True, env=env)
        print(f"Imported {args.reference}")


if __name__ == "__main__":
    main()

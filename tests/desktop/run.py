#!/usr/bin/env python3
"""Run the LXQt and Labwc desktop on Podish's virtual DRM and evdev seat."""

import argparse
import binascii
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import time
import zlib


HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
IMAGE = "localhost/podish-lxqt-smoke:latest"


def write_png(source, destination):
    magic, dimensions, maximum, pixels = source.read_bytes().split(b"\n", 3)
    width, height = map(int, dimensions.split())
    if magic != b"P6" or maximum != b"255" or (width, height) != (640, 480):
        raise ValueError("Expected a 640x480 RGB framebuffer")
    stride = 3 * width
    if len(pixels) != stride * height or len(set(zip(pixels[::3], pixels[1::3], pixels[2::3]))) < 16:
        raise ValueError("Expected a complete, nonuniform desktop frame")

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", binascii.crc32(kind + data))

    rows = b"".join(b"\0" + pixels[offset:offset + stride] for offset in range(0, len(pixels), stride))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    destination.write_bytes(
        b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows)) + chunk(b"IEND", b""))
    return pixels


def check_desktop_pixels(pixels):
    # The window positions and 640x480 mode are fixed by this fixture.
    def region(left, top, right, bottom):
        for y in range(top, bottom):
            row = pixels[3 * (y * 640 + left):3 * (y * 640 + right)]
            yield from zip(row[::3], row[1::3], row[2::3])

    def light_gray(color):
        return min(color) >= 96 and max(color) - min(color) <= 24

    if len(set(region(470, 8, 630, 38))) < 16:
        raise ValueError("LXQt wallpaper is missing from scanout")
    if sum(map(light_gray, region(0, 450, 640, 480))) < 640 * 30 // 2:
        raise ValueError("LXQt panel is missing from scanout")
    text_area = list(region(234, 322, 595, 410))
    if sum(map(light_gray, text_area)) < 100 or text_area.count((0, 0, 0)) < len(text_area) // 2:
        raise ValueError("QTerminal text is missing from scanout")


def run_session(command, work, env, guest_log, exchange, window_markers, timeout):
    deadline = time.monotonic() + timeout
    with guest_log.open("wb") as log, subprocess.Popen(
        command, cwd=work, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT
    ) as process:
        try:
            while process.poll() is None:
                if time.monotonic() >= deadline:
                    raise subprocess.TimeoutExpired(command, timeout)
                guest_output = guest_log.read_text(errors="replace")
                if all(marker in guest_output for marker in window_markers) and guest_output.count("new layer_surface") >= 2:
                    (exchange / "capture-ready").touch()
                    break
                time.sleep(0.2)
            return process.wait(timeout=max(0, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            process.kill()
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rootfs", required=True, type=Path, help="Debian i386 root filesystem tar")
    parser.add_argument("--user", default="user", help="guest account name (default: user)")
    parser.add_argument("--cli", type=Path, default=REPO / "Podish.Cli/bin/Release/net11.0/Podish.Cli.dll")
    parser.add_argument("--out", type=Path, default=REPO / "artifacts/lxqt-desktop")
    parser.add_argument("--verify-input", action="store_true", help="type seat-ok in the desktop terminal and press Enter")
    args = parser.parse_args()
    rootfs = args.rootfs.resolve()
    cli = args.cli.resolve()
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=True)
    screenshot = output / "desktop.png"
    screenshot.unlink(missing_ok=True)
    engine_log = output / "engine.log"
    engine_log.unlink(missing_ok=True)
    guest_log = output / "guest.log"
    command = ["dotnet", str(cli)] if cli.suffix == ".dll" else [str(cli)]
    env = os.environ.copy()
    env.setdefault("PODISH_GLES_GUEST_DIR", str(REPO / "artifacts/podish-gles/guest"))
    env["PODISH_GLES_GUEST_DIR"] = str(Path(env["PODISH_GLES_GUEST_DIR"]).resolve())
    if not args.verify_input:
        env["SDL_VIDEODRIVER"] = "dummy"

    with tempfile.TemporaryDirectory(prefix="podish-lxqt-") as directory:
        work = Path(directory)
        guest = work / "guest"
        guest.mkdir()
        shutil.copyfile(HERE / "labwc.xml", guest / "labwc.xml")
        for script in HERE.glob("*.sh"):
            target = guest / script.name
            shutil.copyfile(script, target)
            target.chmod(0o755)
        subprocess.run([env.get("ZIG", "zig"), "cc", "-target", "x86-linux-gnu.2.11", "-O2",
                        "-Wall", "-Wextra", str(HERE / "frame.c"), "-o", str(guest / "frame")], check=True)
        exchange = work / "output"
        exchange.mkdir()
        # The parent directory is private to this test. The guest user writes the capture through Hostfs.
        exchange.chmod(0o777)
        subprocess.run(command + ["import", str(rootfs), IMAGE], cwd=work, env=env, check=True, timeout=120)
        options = ["--kmsdrm", "--fbdev", "--init", "--fs-backend", "overlay-tmpfs", "--user", args.user,
                   "--log-level", "debug", "--log-file", str(engine_log),
                   "-v", f"{guest}:/run/desktop-test:ro", "-v", f"{exchange}:/run/desktop-output"]
        windows = ["[map] identifier=pcmanfm-qt", "[map] identifier=qterminal"]
        markers = ["Seat opened with backend 'builtin'", "Adding Podish Keyboard", "Adding Podish Mouse",
                   *windows, "LXQt desktop frame captured",
                   "Stopped embedded seatd"]
        if args.verify_input:
            options += ["-e", "PODISH_DESKTOP_INPUT_CHECK=1"]
            markers.append("Wayland keyboard input reached QTerminal")
            print("Click QTerminal in the Podish window, type seat-ok, and press Enter.", flush=True)
        frame = exchange / "desktop.ppm"
        try:
            result = run_session(command + ["run", "--rm", *options, IMAGE, "--", "/run/desktop-test/start.sh"],
                                 work, env, guest_log, exchange, windows, 160 if args.verify_input else 45)
        except subprocess.TimeoutExpired:
            parser.exit(1, f"LXQt desktop smoke timed out. Logs in {output}\n")
        guest_output = guest_log.read_text(errors="replace")
        host_output = engine_log.read_text(errors="replace") if engine_log.is_file() else ""
        missing = [marker for marker in markers if marker not in guest_output]
        if guest_output.count("new layer_surface") < 2:
            missing.append("LXQt panel and desktop layer surfaces")
        if "Presented framebuffer mode 640x480" not in host_output:
            missing.append("SDL presentation")
        if "inotify read():" in guest_output:
            parser.exit(1, f"LXQt desktop smoke failed: GLib could not read an inotify event. Logs in {output}\n")
        if result or missing:
            parser.exit(1, f"LXQt desktop smoke failed (exit {result}, missing {missing})\n"
                           f"Logs in {output}\n")
        try:
            check_desktop_pixels(write_png(frame, screenshot))
        except (OSError, ValueError) as error:
            parser.exit(1, f"LXQt desktop smoke failed: {error}. Artifacts in {output}\n")
    print(f"LXQt desktop smoke passed. Screenshot at {screenshot}")


if __name__ == "__main__":
    main()

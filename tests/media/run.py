"""Run the same IA-32 media probes against a Windows or Linux Podish CLI."""

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


CASES = {
    "fbdev": (["--fbdev", "--fbdev-size", "96x64"], "podish-fbdev-probe",
              "fbdev smoke passed", "Presented framebuffer mode 96x64"),
    "wayland": (["--wayland-server"], "podish-wayland-probe",
                "Wayland shm smoke passed", "Wayland SDL host presented surfaces=1"),
    "kms": (["--kmsdrm", "--fbdev"], "podish-kmsdrm-probe",
            "DRM framebuffer preview passed", "Presented framebuffer mode 640x480"),
    "pulse": (["--pulse-server"], "podish-pulse-probe",
              "PulseAudio smoke passed", "opened default SDL audio device driver="),
    "fbdev-egl": (["--fbdev", "--fbdev-size", "96x64", "--gles"], "podish-gles-fbdev-probe",
                  "fbdev EGL/GLES smoke passed", "Presented framebuffer mode 96x64"),
    "fbdev-es3": (["--fbdev", "--fbdev-size", "96x64", "--gles", "-e", "PODISH_TEST_FBDEV=1"],
                  "podish-gles3-probe", "GLES 3.0 probe passed", "Presented framebuffer mode 96x64"),
    "wayland-egl": (["--wayland-server", "--gles"], "podish-wayland-egl-probe",
                    "Wayland EGL smoke passed", "Wayland SDL host presented surfaces=1"),
    "kms-egl": (["--kmsdrm", "--gles"], "podish-kmsdrm-egl-probe",
                "KMS GBM EGL smoke passed", "KMS EGL presented a GPU frame"),
    "kms-es3": (["--kmsdrm", "--gles", "-e", "PODISH_TEST_GLES3=1"], "podish-kmsdrm-egl-probe",
                "KMS GBM EGL smoke passed", "KMS EGL presented a GPU frame"),
}
BASE_CASES = ("fbdev", "wayland", "kms", "pulse")
IMAGE = "localhost/podish-media-smoke:latest"
STK_OPTIONS = ["--kmsdrm", "--gles", "-m", "2G", "-e", "SDL_VIDEODRIVER=kmsdrm",
               "-e", "SDL_AUDIODRIVER=dummy", "-e", "SDL_VIDEO_GL_DRIVER=libGLESv2.so.2",
               "-e", "ALSOFT_DRIVERS=null"]
STK_ARGS = ["--log=nocolor", "--no-sound", "--race-now", "--track=lighthouse",
            "--kart=tux", "--numkarts=3", "--ai=puffy,gavroche", "--screensize=640x480",
            "--seed=23", "--profile-time=30"]
KMS_RENDERER = "KMS EGL renderer: "
KMS_FRAME = "KMS EGL presented a GPU frame"


def main():
    sys.stdout.reconfigure(errors="backslashreplace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path, help="Podish.Cli.dll or native executable")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet launcher for a DLL CLI")
    parser.add_argument("--fixture", required=True, type=Path, help="build-fixture.sh output directory")
    parser.add_argument("--headless", action="store_true", help="use SDL dummy video/audio; no visible or audible output")
    parser.add_argument("--case", action="append", choices=CASES, help="repeat to select cases; default: fbdev, Wayland, KMS, Pulse")
    parser.add_argument("--stk-rootfs", type=Path, help="run only the 30-second SuperTuxKart KMS race using this rootfs tar")
    parser.add_argument("--logs", type=Path, help="keep guest and engine logs in this directory")
    parser.add_argument("--require-renderer", help="require this substring in the STK host GL_RENDERER")
    args = parser.parse_args()
    cli = args.cli.resolve()
    fixture = args.fixture.resolve()
    env = os.environ.copy()
    env["PODISH_GLES_GUEST_DIR"] = str(fixture / "guest-graphics")
    if args.stk_rootfs and (args.case or args.headless):
        parser.error("the STK race runs by itself and requires a real SDL window")
    if args.require_renderer and not args.stk_rootfs:
        parser.error("--require-renderer requires --stk-rootfs")
    if args.headless:
        env.update(SDL_VIDEODRIVER="dummy", SDL_AUDIODRIVER="dummy")
        if any(case in ("kms-egl", "kms-es3") for case in (args.case or ())):
            parser.error("KMS EGL cases require a real SDL window system")
    if (args.stk_rootfs or any("--gles" in CASES[case][0] for case in (args.case or ()))) and not env.get("PODISH_ANGLE_DIR"):
        parser.error("EGL cases require PODISH_ANGLE_DIR")
    if args.logs:
        args.logs.mkdir(parents=True, exist_ok=True)
        args.logs = args.logs.resolve()
    command = [shutil.which(args.dotnet) or args.dotnet, str(cli)] if cli.suffix == ".dll" else [str(cli)]
    with tempfile.TemporaryDirectory(prefix="podish-media-") as work:
        def invoke(arguments, timeout):
            return subprocess.run(command + arguments, cwd=work, env=env, stdin=subprocess.DEVNULL,
                                  stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)

        rootfs = args.stk_rootfs.resolve() if args.stk_rootfs else fixture / "rootfs.tar"
        imported = invoke(["import", str(rootfs), IMAGE], 180)
        if imported.returncode:
            print(imported.stdout.decode(errors="replace"))
            return 1
        failed = 0
        for name in (("stk",) if args.stk_rootfs else args.case or BASE_CASES):
            game = name == "stk"
            if game:
                options, probe, guest_marker, host_marker = (
                    STK_OPTIONS, "supertuxkart-gles-sse2", "profile: Number of frames:", KMS_FRAME)
            else:
                options, probe, guest_marker, host_marker = CASES[name]
            log = (args.logs or Path(work)) / f"{name}.engine.log"
            log.unlink(missing_ok=True)
            try:
                result = invoke(["run", "--rm", "--network", "private", "--fs-backend", "overlay-tmpfs",
                                 "--log-level", "debug",
                                 "--log-file", str(log), *options, IMAGE, "--", f"/usr/local/bin/{probe}",
                                 *(STK_ARGS if game else ())], 600 if game else 45)
            except subprocess.TimeoutExpired as error:
                output = error.stdout or b""
                if args.logs:
                    (args.logs / f"{name}.guest.log").write_bytes(output)
                print(f"FAIL {name}: timed out\n{output[-12000:].decode(errors='replace')}", flush=True)
                failed += 1
                continue
            if args.logs:
                (args.logs / f"{name}.guest.log").write_bytes(result.stdout)
            output = result.stdout.decode(errors="replace")
            host_log = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
            evidence = next((line for line in (host_log + "\n" + output).splitlines() if host_marker in line), None)
            guest_ok = guest_marker in output
            if game:
                profile = re.search(r"profile: Number of frames:\s*(\d+)", output)
                swaps = host_log.count(KMS_FRAME)
                renderer = next((line.split(KMS_RENDERER, 1)[1] for line in host_log.splitlines()
                                 if KMS_RENDERER in line), "")
                guest_ok = bool(profile and int(profile[1]) >= 8)
                if swaps < 8 or not renderer or (args.require_renderer and args.require_renderer not in renderer):
                    evidence = None
                print(f"STK profile frames={profile[1] if profile else 0}, host KMS swaps={swaps}, renderer={renderer}")
            if result.returncode or not guest_ok or not evidence:
                print(f"FAIL {name}: exit={result.returncode}, guest={guest_ok}, host={bool(evidence)}")
                print(host_log[-6000:], end="")
                print(output[-12000:], flush=True)
                failed += 1
            else:
                print(f"PASS {name}: {evidence.strip()}", flush=True)
        return int(failed != 0)


if __name__ == "__main__":
    raise SystemExit(main())

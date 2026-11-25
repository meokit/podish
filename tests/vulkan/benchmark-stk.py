#!/usr/bin/env python3
import argparse
import json
from pathlib import Path
import re
import shlex


BACKENDS = {"guest-zink": "zink", "host-zink": "virgl"}
RENDERER = re.compile(r"IrrDriver: OpenGL renderer: ([^\r\n]+)")
PROFILE = re.compile(r"Number of frames: (\d+) time ([\d.]+), Average FPS: ([\d.]+)")
EXIT = re.compile(r"\bPODISH_STK_EXIT=(\d+)")
ANSI_COLOR = re.compile(r"\x1b\[[\d;]*m")


def guest_command(backend, seconds):
    game = shlex.join([
        "/usr/games/supertuxkart", "--render-driver=gl", "--log=nocolor", "--no-sound", "--race-now",
        "--track=lighthouse", "--kart=tux", "--numkarts=1",
        "--screensize=640x480", "--seed=23", f"--profile-time={seconds}",
    ])
    return "\n".join([
        f"export PODISH_GUEST_GL_DRIVER={BACKENDS[backend]}",
        ". /usr/local/libexec/podish-vulkan-env || exit $?",
        'gl_libraries=/usr/lib/i386-linux-gnu/libGL.so.1:/usr/lib/i386-linux-gnu/libEGL.so.1',
        'for gallium in /usr/local/lib/podish-zink/lib/libgallium-*.so; do',
        '    if [ -r "$gallium" ]; then gl_libraries="$gl_libraries:$gallium"; break; fi',
        'done',
        f'LD_PRELOAD="$gl_libraries${{LD_PRELOAD:+:$LD_PRELOAD}}" {game}',
        'printf \'\\nPODISH_STK_EXIT=%s\\n\' "$?"',
    ])


def read_guest_log(path):
    parts = []
    for line in path.read_text(errors="replace").splitlines(keepends=True):
        if line.startswith("{"):
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                pass
            else:
                if "Log" in record:
                    parts.append(record["Log"])
                    continue
        parts.append(line)
    return ANSI_COLOR.sub("", "".join(parts))


def summarize(backend, log, exit_code):
    renderer_matches = RENDERER.findall(log)
    renderer = renderer_matches[-1].strip() if renderer_matches else None
    stats = PROFILE.findall(log)
    if exit_code is None:
        exits = EXIT.findall(log)
        exit_code = int(exits[-1]) if exits else None
    accelerated = renderer and "zink" in renderer.lower() and "llvmpipe" not in renderer.lower()
    renderer_matches_backend = accelerated and (("Venus" in renderer) == (backend == "guest-zink"))

    if exit_code is None:
        state = "incomplete"
    elif renderer is None:
        state = "initialization-failed"
    elif not renderer_matches_backend:
        state = "renderer-mismatch"
    elif not stats:
        state = "aborted-before-profile"
    elif exit_code != 0:
        state = "failed-after-profile"
    else:
        state = "completed"

    result = {"backend": backend, "state": state, "exit_code": exit_code, "renderer": renderer}
    if stats:
        frames, wall_time, fps = stats[-1]
        result["frames"] = int(frames)
        if state == "completed":
            result["seconds"] = float(wall_time)
            result["fps"] = float(fps)
    return result


def main():
    parser = argparse.ArgumentParser(description="Run a comparable SuperTuxKart Lighthouse OpenGL profile and summarize the result.")
    actions = parser.add_subparsers(dest="action", required=True)
    command = actions.add_parser("command", help="Print the guest command for PODISH_AUTOSTART_APP")
    command.add_argument("backend", choices=BACKENDS)
    command.add_argument("--seconds", type=int, default=3)
    report = actions.add_parser("report", help="Summarize a console log or ctr.log after the guest exits")
    report.add_argument("backend", choices=BACKENDS)
    report.add_argument("log", type=Path)
    report.add_argument("--exit-code", type=int, help="Explicit exit status for existing logs")
    args = parser.parse_args()
    if args.action == "command":
        if args.seconds < 1:
            parser.error("--seconds must be positive")
        print(guest_command(args.backend, args.seconds))
        return 0

    result = summarize(args.backend, read_guest_log(args.log), args.exit_code)
    print(json.dumps(result, indent=2))
    return 0 if result["state"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())

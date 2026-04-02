#!/usr/bin/env python3
import argparse
import os
import sys
import time

import pexpect


PROMPT = r"/ #"


def expect_prompt(child: pexpect.spawn, timeout: int, label: str) -> None:
    child.expect(PROMPT, timeout=timeout)


def run_command(child: pexpect.spawn, command: str, timeout: int, label: str) -> None:
    child.sendline(command)
    expect_prompt(child, timeout=timeout, label=label)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--build-timeout", type=int, default=600)
    parser.add_argument("--startup-timeout", type=int, default=180)
    parser.add_argument("--apk-timeout", type=int, default=180)
    parser.add_argument("--clone-timeout", type=int, default=20)
    parser.add_argument("--image", default="docker.io/i386/alpine:latest")
    args = parser.parse_args()

    repo = os.path.abspath(args.repo or os.path.join(os.path.dirname(__file__), ".."))
    run_cmd = (
        f"dotnet run {'--no-build ' if args.skip_build else ''}"
        "--project Podish.Cli/Podish.Cli.csproj -- "
        f"run -it --rm {args.image}"
    )

    child = pexpect.spawn(
        "/bin/bash",
        ["-lc", run_cmd],
        cwd=repo,
        encoding="utf-8",
        timeout=max(args.startup_timeout, args.apk_timeout, args.clone_timeout, 30),
    )
    child.logfile = sys.stdout

    try:
        expect_prompt(child, args.startup_timeout, "container prompt")
        run_command(child, "apk add git", args.apk_timeout, "apk add git")
        child.sendline("rm -rf coremark")
        expect_prompt(child, timeout=30, label="cleanup")

        start = time.monotonic()
        child.sendline("git clone https://github.com/eembc/coremark")
        expect_prompt(child, args.clone_timeout, "git clone coremark")
        elapsed = time.monotonic() - start
        print(f"\n[repro] git clone completed in {elapsed:.2f}s", flush=True)

        child.sendline("exit")
        child.expect(pexpect.EOF, timeout=30)
        return 0
    except pexpect.TIMEOUT:
        print("\n[repro] timeout hit; treating as failure", file=sys.stderr, flush=True)
        try:
            child.sendcontrol("c")
            child.sendline("exit")
            child.expect(pexpect.EOF, timeout=5)
        except Exception:
            child.close(force=True)
        return 1
    except pexpect.EOF:
        print("\n[repro] process exited unexpectedly; skipping commit", file=sys.stderr, flush=True)
        return 125


if __name__ == "__main__":
    raise SystemExit(main())

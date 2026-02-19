#!/usr/bin/env python3
import os
import sys
import time

import pexpect


def main() -> int:
    repo = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    cmd = (
        "dotnet build Fiberish.Cli/Fiberish.Cli.csproj && "
        "dotnet run --project Fiberish.Cli/Fiberish.Cli.csproj -- "
        "--trace --rootfs tests/linux/rootfs tests/linux/rootfs/bin/hush"
    )

    child = pexpect.spawn("/bin/bash", ["-lc", cmd], cwd=repo, encoding="utf-8", timeout=20)
    child.logfile = sys.stdout

    # Wait for hush prompt
    child.expect(r"~ #")

    # Open vi on emulator.log
    child.sendline("vi emulator.log")

    # Give vi a moment to start
    time.sleep(1)

    # Resize the terminal to trigger SIGWINCH
    child.setwinsize(45, 120)
    time.sleep(0.2)
    child.setwinsize(50, 180)
    time.sleep(0.2)

    # Try to exit vi cleanly.
    # Send ESC first, wait a bit so vi leaves any pending mode, then send :q!
    child.send("\x1b")
    time.sleep(0.2)
    child.sendline(":q!")

    # Verify shell is back by issuing a marker command.
    marker = "__VI_EXITED__"
    child.sendline(f"echo {marker}")
    child.expect(marker, timeout=10)
    child.sendline("exit")
    child.expect(pexpect.EOF, timeout=10)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

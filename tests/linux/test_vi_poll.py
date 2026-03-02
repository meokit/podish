import pexpect
import os
import sys

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
EMULATOR_LOG = os.path.join(PROJECT_ROOT, "emulator.log")


def test_vi_poll_spam(fiberpod_dll, alpine_image):
    """
    Runs busybox vi under the emulator with --trace, then checks the log for
    excessive poll busy-wait messages.

    The `build_cli` fixture (from conftest.py) ensures a single build per session.
    We use the pre-built DLL directly to skip redundant build checks.
    """
    # Clean old log
    if os.path.exists(EMULATOR_LOG):
        os.remove(EMULATOR_LOG)

    vi_bin = os.path.join(alpine_image, "bin/busybox")
    if not os.path.exists(vi_bin):
        import pytest
        pytest.skip(f"busybox binary not found at {vi_bin}")

    args = [
        fiberpod_dll,
        "--log-level", "Trace",
        "--log-file", EMULATOR_LOG,
        "run",
        "-i",
        "-t",
        alpine_image,
        "--",
        "/bin/busybox",
        "vi",
    ]
    print(f"\nRunning: dotnet {' '.join(args)}")
    child = pexpect.spawn("dotnet", args, encoding='utf-8', timeout=20, cwd=PROJECT_ROOT)

    try:
        # Wait for vi to show its first screen character ('~' marks empty lines).
        # This is faster and more reliable than a fixed sleep(5).
        child.expect(r"~", timeout=15)

        # Send quit command
        child.send(":q\r")

        # Wait for vi to exit cleanly
        child.expect(pexpect.EOF, timeout=10)

    except pexpect.exceptions.TIMEOUT:
        print("Timeout waiting for vi to exit. Sending Ctrl+C...")
        child.sendintr()
        child.close()
    except Exception as e:
        print(f"Error: {e}")
        child.close()

    # Inspect log for busy-wait spam
    print("Checking emulator.log for spam...")
    if not os.path.exists(EMULATOR_LOG):
        print("Error: emulator.log not found!")
        sys.exit(1)

    spam_count = 0
    with open(EMULATOR_LOG, 'r') as f:
        for line in f:
            if "no fds ready, scheduling timer re-poll" in line:
                spam_count += 1

    print(f"Found {spam_count} occurrences of busy-wait log.")

    # Before the fix this was thousands per second; now it should be near zero.
    # A small number is tolerated in case vi legitimately times out a poll.
    if spam_count > 100:
        print("FAIL: Too much log spam!")
        sys.exit(1)
    else:
        print("PASS: Log spam eliminated/reduced.")


if __name__ == "__main__":
    test_vi_poll_spam(fiberpod_dll="")

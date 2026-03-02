import pytest
import pexpect
import os

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
LINUX_ASSETS = os.path.join(PROJECT_ROOT, "tests/linux/assets")
TEST_ASSET = os.path.join(PROJECT_ROOT, "tests/linux/assets/test_execve_vdso")


def test_execve_vdso_persistence(fiberpod_dll, alpine_image):
    """
    Verifies that SysExecve properly re-maps vDSO and maintains signal capabilities.
    The `build_cli` fixture (from conftest.py) ensures the project is built once
    per session before any test in tests/linux/ runs.
    """
    if not os.path.exists(TEST_ASSET):
        pytest.skip(f"Test asset {TEST_ASSET} not found. Did you run 'zig cc ...'?")

    args = [
        fiberpod_dll,
        "run",
        "-v", f"{LINUX_ASSETS}:/tests",
        alpine_image,
        "--",
        "/tests/test_execve_vdso",
        "arg1",
        "arg2",
    ]
    print(f"\nRunning: dotnet {' '.join(args)}")
    child = pexpect.spawn("dotnet", args, encoding='utf-8', timeout=10, cwd=PROJECT_ROOT)

    try:
        # 1. First run
        child.expect(r"PID \d+: Starting \(Argc=3\)") # argv[0] + arg1 + arg2
        child.expect(r"PID \d+: Raising SIGUSR1")

        # 2. Signal Handling (requires vDSO/SigReturn)
        try:
            child.expect(r"Signal 10 handled")
        except pexpect.exceptions.EOF:
             print("Output before EOF:")
             print(repr(child.before))
             raise

        child.expect(r"PID \d+: Continued after signal")

        # 3. Execve
        child.expect(r"PID \d+: Executing self")

        # 4. Second run (re-exec) — argc should be 2: argv[0] + "re-exec"
        child.expect(r"PID \d+: Starting \(Argc=2\)")
        child.expect(r"PID \d+: Raising SIGUSR1")
        child.expect(r"Signal 10 handled")
        child.expect(r"PID \d+: Continued after signal")
        child.expect(r"PID \d+: Re-executed successfully")

        # Expect clean exit
        child.expect(pexpect.EOF)

        child.close()
        print(f"Exit Status: {child.exitstatus}")
        assert child.exitstatus == 0

    except pexpect.exceptions.TIMEOUT:
        print("Timeout! Output so far:")
        print(repr(child.before))
        raise
    except pexpect.exceptions.EOF:
        print("Premature EOF! Output so far:")
        print(repr(child.before))
        child.close()
        print(f"Exit Status: {child.exitstatus}")
        if child.exitstatus != 0:
             pytest.fail(f"Process exited with error code {child.exitstatus}")

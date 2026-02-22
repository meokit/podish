import pytest
import pexpect
import os

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
CLI_PROJECT = os.path.join(PROJECT_ROOT, "Fiberish.Cli")
CLI_DLL = os.path.join(CLI_PROJECT, "bin", "Debug", "net8.0", "Fiberish.Cli.dll")
TEST_ASSET = os.path.join(PROJECT_ROOT, "tests/linux/assets/test_execve_vdso")


def test_execve_vdso_persistence(build_cli):
    """
    Verifies that SysExecve properly re-maps vDSO and maintains signal capabilities.
    The `build_cli` fixture (from conftest.py) ensures the project is built once
    per session before any test in tests/linux/ runs.
    """
    if not os.path.exists(TEST_ASSET):
        pytest.skip(f"Test asset {TEST_ASSET} not found. Did you run 'zig cc ...'?")

    # Use the pre-built DLL directly instead of `dotnet run --project` to avoid
    # a redundant build check on every test invocation.
    cmd = f"dotnet {CLI_DLL} {TEST_ASSET} arg1 arg2"

    print(f"\nRunning: {cmd}")

    child = pexpect.spawn(cmd, encoding='utf-8', timeout=10)

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

import pytest
import pexpect
import os
import subprocess
import time

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
CLI_PROJECT = os.path.join(PROJECT_ROOT, "Fiberish.Cli")
TEST_ASSET = os.path.join(PROJECT_ROOT, "tests/linux/assets/test_execve_vdso")

@pytest.fixture(scope="module")
def build_cli():
    """Builds the Fiberish.Cli project once."""
    print("Building Fiberish.Cli...")
    subprocess.run(["dotnet", "build", CLI_PROJECT, "-c", "Debug"], check=True)
    return os.path.join(CLI_PROJECT, "bin", "Debug", "net8.0", "Fiberish.Cli")

def test_execve_vdso_persistence(build_cli):
    """
    Verifies that SysExecve properly re-maps vDSO and maintains signal capabilities.
    """
    if not os.path.exists(TEST_ASSET):
        pytest.skip(f"Test asset {TEST_ASSET} not found. Did you run 'zig cc ...'?")

    # Command to run: dotnet run --project Fiberish.Cli -- <asset>
    # or use the built binary directly if possible, but dotnet run is safer for deps
    cmd = f"dotnet run --project {CLI_PROJECT} -- {TEST_ASSET} arg1 arg2"
    
    print(f"Running: {cmd}")
    
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
        
        # 4. Second run (re-exec)
        # Argc should be 2: argv[0] + "re-exec"
        child.expect(r"PID \d+: Starting \(Argc=2\)") 
        child.expect(r"PID \d+: Raising SIGUSR1")
        child.expect(r"Signal 10 handled")
        child.expect(r"PID \d+: Continued after signal")
        child.expect(r"PID \d+: Re-executed successfully")
        
        # Expect clean exit
        child.expect(pexpect.EOF)
        
        # Check exit code
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

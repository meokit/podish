import pytest
import subprocess
import os
import time

def test_flock_cleanup_on_exit(tmp_path):
    # This test verifies whether an exclusive flock held by a process is automatically
    # released when the process exits. We spawn a python script via FiberPod that
    # locks a file and then exits. Immediately after, we spawn another script that
    # verifies it can acquire the lock.

    # We will use busybox flock to acquire the lock and busybox sh to sleep.
    # Script 1: Wait 2 seconds holding the lock, but we'll kill process 1 early.
    # Actually it's easier to just run a shell script that holds the flock and then exits. 
    # If the emulator cleans up correctly, the FDs are closed.

    script1 = tmp_path / "locker.sh"
    script1.write_text("""#!/bin/sh
# Lock FD 200, output "locked" to stdout, sleep a bit, then exit
exec 200>/dev/shm/test.lock
flock -x 200
echo "locked1"
# Exit! We don't unlock or close FD 200.
exit 0
    """)
    script1.chmod(0o755)

    script2 = tmp_path / "verifier.sh"
    script2.write_text("""#!/bin/sh
exec 200>/dev/shm/test.lock
# Non-blocking lock. If the previous process leaked the lock, this will fail.
flock -x -n 200 || exit 1
echo "locked2"
exit 0
    """)
    script2.chmod(0o755)

    fiberpod_exe = "dotnet"
    
    # Run locker
    fiberpod_args = [fiberpod_exe, "run", "--project", "Podish.Cli/Podish.Cli.csproj", "--", "run", "-v", f"{str(tmp_path)}:/scripts", "docker.io/i386/alpine:latest", "/bin/sh", "/scripts/locker.sh"]
    proc1 = subprocess.run(fiberpod_args, capture_output=True, text=True)
    assert proc1.returncode == 0, f"Locker failed: {proc1.stderr}\nStdout: {proc1.stdout}"
    assert "locked1" in proc1.stdout, f"Locker didn't output locked1: {proc1.stdout}"

    # Run verifier
    fiberpod_args_2 = [fiberpod_exe, "run", "--project", "Podish.Cli/Podish.Cli.csproj", "--", "run", "-v", f"{str(tmp_path)}:/scripts", "docker.io/i386/alpine:latest", "/bin/sh", "/scripts/verifier.sh"]
    proc2 = subprocess.run(fiberpod_args_2, capture_output=True, text=True)
    
    # Assert verifier succeeded, meaning it acquired the lock
    assert proc2.returncode == 0, f"Verifier failed to acquire lock! stderr: {proc2.stderr}\nStdout: {proc2.stdout}"
    assert "locked2" in proc2.stdout, f"Verifier didn't output locked2: {proc2.stdout}"

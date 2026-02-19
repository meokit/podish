import pexpect
import os
import subprocess
import time
import sys

PROJECT_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
CLI_PROJECT = os.path.join(PROJECT_ROOT, "Fiberish.Cli")
EMULATOR_LOG = os.path.join(PROJECT_ROOT, "emulator.log")

def test_vi_poll_spam():
    # 1. Build
    print("Building Fiberish.Cli...")
    subprocess.run(["dotnet", "build", CLI_PROJECT, "-c", "Debug"], check=True)

    # 2. Clean log
    if os.path.exists(EMULATOR_LOG):
        os.remove(EMULATOR_LOG)

    # 3. Run vi
    # We use busybox vi
    cmd = f"dotnet run --project {CLI_PROJECT} -- --trace --rootfs tests/linux/rootfs tests/linux/rootfs/bin/busybox vi"
    print(f"Running: {cmd}")

    child = pexpect.spawn(cmd, encoding='utf-8', timeout=20)
    
    try:
        # vi starts, clears screen, etc.
        # We assume it eventually reaches interactive state. 
        # Busybox vi might print version info or just show empty buffer.
        time.sleep(5) 
        
        # Send Exit command
        child.send(":q\r") # Enter
        
        # Expect exit
        child.expect(pexpect.EOF)
        
    except pexpect.exceptions.TIMEOUT:
        print("Timeout waiting for vi to exit. Sending Ctrl+C...")
        child.sendintr()
        child.close()
    except Exception as e:
        print(f"Error: {e}")
        child.close()

    # 4. Check log
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
    
    # Ideally should be 0 or very low (maybe 1-2 if race condition at startup?)
    # But since we register waits, it should never happen unless we timeout.
    # And we don't expect timeouts in normal vi idle loop? 
    # Actually vi uses poll with timeout. If it times out, it re-polls.
    # If vi sets a timeout, and we time out, we might log "timeout" or just "ready=0".
    # The spam message "no fds ready, scheduling timer re-poll" was specifically when
    # we *scheduled* a re-poll because we *didn't* block properly.
    # With new logic, we register waits and return. We don't loop endlessly printing that message.
    
    if spam_count > 100: # Arbitrary threshold, before it was thousands per second
        print("FAIL: Too much log spam!")
        sys.exit(1)
    else:
        print("PASS: Log spam eliminated/reduced.")

if __name__ == "__main__":
    test_vi_poll_spam()

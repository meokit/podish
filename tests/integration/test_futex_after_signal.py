"""
Regression test for Bug #2:
    When a shared futex wait is interrupted by a signal, the Waiter was never
    removed from _sharedQueues (CancelWaitShared was not called). A subsequent
    futex_wake would silently consume the defunct waiter instead of the real
    next waiter, causing the next wait to hang forever.

    After the fix, CancelWaitShared is called before returning -ERESTARTSYS,
    so the queue remains clean and later wait/wake pairs work correctly.
"""
import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case


@pytest.mark.integration
def test_futex_after_signal(
    project_root: Path,
    integration_assets_dir: Path,
    fiberpod_dll: str,
    alpine_image: str,
) -> None:
    case = EmulatorCase(
        name="futex_after_signal",
        binary_name="test_futex_after_signal",
        expect_tokens=[
            "[Child] Phase 1: interrupted by signal",
            "[Child] Phase 2: woken!",
            "[Child] SUCCESS",
            "SUCCESS",
        ],
        reject_tokens=["FAILED", "hung"],
        # This test involves a SIGALRM interrupt followed by a real wake;
        # give it extra time to account for the signal-handling roundtrip.
        timeout=30,
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

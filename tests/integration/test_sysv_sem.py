from __future__ import annotations

from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_sysv_semaphores(project_root: Path) -> None:
    case = EmulatorCase(
        name="sysv_semaphores",
        binary_name="test_sysv_sem",
        expect_tokens=[
            "Starting SysV Semaphores Test",
            "Semaphore created and initialized to 0",
            "Parent: Waiting for semaphore to be 1...",
            "Child: Sleeping for 1 second...",
            "Child: Incrementing semaphore",
            "Child: Exiting",
            "Parent: Semaphore acquired!",
            "Parent: Trying IPC_NOWAIT decrement...",
            "Parent: Received EAGAIN as expected",
            "Parent: Semaphore removed",
            "Test Passed"
        ],
    )
    
    run_case(project_root, project_root / "build/integration-assets/assets", case)

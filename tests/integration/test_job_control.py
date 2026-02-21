from __future__ import annotations

from pathlib import Path

import pexpect
import pytest

from .harness import EmulatorCase, run_case


@pytest.mark.integration
def test_tty_background_read_sigttin(project_root: Path) -> None:
    # Use the compiled test binary from earlier
    case = EmulatorCase(
        name="job_control",
        binary_name="test_job_control",
        expect_tokens=[
            "Received SIGTTIN (child stopped)",
            "Parent exit",
        ],
    )
    
    # Run the case directly
    run_case(project_root, project_root / "tests/bin", case)

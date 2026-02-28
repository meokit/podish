from __future__ import annotations

from pathlib import Path

import pexpect
import pytest

from .harness import EmulatorCase, run_case


@pytest.mark.integration
def test_tty_background_read_sigttin(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    # Use the compiled test binary from earlier
    case = EmulatorCase(
        name="job_control",
        binary_name="test_job_control",
        use_tty=True,
        expect_tokens=[
            "Received SIGTTIN",
            "Received SIGTTOU",
            "Parent exit",
        ],
    )
    
    # Run the case directly
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

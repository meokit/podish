from pathlib import Path
import pytest
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_proc_fd_magic(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str | None, alpine_image: str) -> None:
    case = EmulatorCase(
        name="test_proc_fd_magic",
        binary_name="test_proc_fd_magic",
        expect_tokens=[
            "--- Testing /proc/self/fd magic link reopen ---",
            "Successfully reopened via /proc/self/fd/",
            "PASS: Content verified: Magic Link Test Data",
            "Proc FD Magic Link test PASSED!"
        ],
        timeout=30,
    )
    
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

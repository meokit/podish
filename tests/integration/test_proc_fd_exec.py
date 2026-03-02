from pathlib import Path
import pytest
from .harness import EmulatorCase, run_case


@pytest.mark.integration
def test_proc_fd_exec(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str | None, alpine_image: str) -> None:
    case = EmulatorCase(
        name="test_proc_fd_exec",
        binary_name="test_proc_fd_exec",
        expect_tokens=[
            "--- Testing execve via /proc/self/fd/7 ---",
            "PROC_FD_EXEC_OK",
        ],
        reject_tokens=[
            "FAIL: execve(/proc/self/fd/7) failed",
            "execve: No such file or directory",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

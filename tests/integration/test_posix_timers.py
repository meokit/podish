import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_posix_timers(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="posix_timers",
        binary_name="test_posix_timers",
        expect_tokens=[
            "Starting POSIX Timers Test",
            "Timer created successfully",
            "Timer set. Waiting for ticks...",
            "Timer fired! sig=14 value=42",
            "Timer fired 2 times. Deleting timer...",
            "Test Passed"
        ],
    )

    run_case(project_root, project_root / "build/integration-assets/assets", case, fiberpod_dll, alpine_image)

import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_rt_signals(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="rt_signals",
        binary_name="test_rt_signals",
        expect_tokens=[
            "--- Testing RT signal queuing ---",
            "RT signal queuing OK",
            "--- Testing standard signal saturation ---",
            "SIGUSR1 received: 1",
            "RT signals test PASSED!",
        ],
    )

    run_case(project_root, project_root / "build/integration-assets/assets", case, fiberpod_dll, alpine_image)

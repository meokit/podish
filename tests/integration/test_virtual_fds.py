import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_virtual_fds(project_root: Path) -> None:
    case = EmulatorCase(
        name="virtual_fds",
        binary_name="test_virtual_fds",
        expect_tokens=[
            "--- Testing eventfd ---",
            "eventfd read: 20",
            "--- Testing timerfd ---",
            "timerfd expired",
            "--- Testing signalfd ---",
            "Sending SIGUSR1 to self...",
            "signalfd read SIGUSR1",
            "Virtual FDs test PASSED!"
        ],
    )

    run_case(project_root, project_root / "build/integration-assets/assets", case)

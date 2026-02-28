import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_network(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="network",
        binary_name="test_network",
        expect_tokens=[
            "--- Testing epoll and socketpair ---",
            "Verified EAGAIN on empty read",
            "Wrote message to sv[0]",
            "epoll_wait returned: 1 event",
            "Message read successfully: hello epoll",
            "Verified empty epoll_wait",
            "Network test PASSED!"
        ],
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

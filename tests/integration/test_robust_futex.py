import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_robust_futex(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="robust_futex",
        binary_name="test_robust_futex",
        expect_tokens=[
            "[Child] Registering robust list head at",
            "[Child] Acquiring futex, tid=",
            "[Child] Exiting without unlocking futex.",
            "[Parent] Child exited with status 0x0",
            "[Parent] SUCCESS: FUTEX_OWNER_DIED bit is set!",
        ],
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

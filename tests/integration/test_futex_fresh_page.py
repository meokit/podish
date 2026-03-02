"""
Regression test for Bug #1:
    futex_wait on a fresh (un-faulted) MAP_SHARED anonymous page used to
    return EFAULT because GetPhysicalAddressSafe was called before CopyFromUser.

    After the fix, CopyFromUser faults in the page first, so the physical key
    resolves correctly and the wait succeeds.
"""
import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case


@pytest.mark.integration
def test_futex_fresh_page(
    project_root: Path,
    integration_assets_dir: Path,
    fiberpod_dll: str,
    alpine_image: str,
) -> None:
    case = EmulatorCase(
        name="futex_fresh_page",
        binary_name="test_futex_fresh_page",
        expect_tokens=[
            "[Child] futex_wait returned OK",
            "[Parent] futex_wake woke",
            "SUCCESS",
        ],
        reject_tokens=["EFAULT", "regression"],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

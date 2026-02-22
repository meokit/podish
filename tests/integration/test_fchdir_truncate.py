import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_fchdir_truncate(project_root: Path) -> None:
    case = EmulatorCase(
        name="fchdir_truncate_test",
        binary_name="test_fchdir_truncate",
        expect_tokens=[
            "Starting fchdir and truncate tests...",
            "fchdir OK, inside testdir_fchdir",
            "ftruncate OK, size is 5",
            "truncate OK, size is 100",
            "All checks PASSED!"
        ],
    )

    run_case(project_root, project_root / "build/integration-assets/assets", case)

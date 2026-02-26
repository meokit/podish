import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_nanosleep(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="nanosleep_test",
        binary_name="test_nanosleep",
        expect_tokens=[
            "Starting nanosleep test...",
            "Elapsed time:",
            "Nanosleep test PASSED!"
        ],
    )

    run_case(project_root, project_root / "build/integration-assets/assets", case, fiberpod_dll, alpine_image)

from pathlib import Path

from .harness import EmulatorCase, run_case


def test_private_tcp_poll(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="private_tcp_poll",
        binary_name="test_private_tcp_poll",
        network_mode="private",
        expect_tokens=[
            "--- Testing private TCP poll readiness ---",
            "Connect readiness observed",
            "Read readiness observed",
            "Private TCP poll test PASSED!",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

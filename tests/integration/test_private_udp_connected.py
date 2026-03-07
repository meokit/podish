from pathlib import Path

from .harness import EmulatorCase, run_case


def test_private_udp_connected(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="private_udp_connected",
        binary_name="test_private_udp_connected",
        network_mode="private",
        expect_tokens=[
            "--- Testing private connected UDP ---",
            "Connected UDP request/reply PASSED!",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

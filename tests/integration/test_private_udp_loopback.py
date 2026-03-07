from pathlib import Path

from .harness import EmulatorCase, run_case


def test_private_udp_loopback(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="private_udp_loopback",
        binary_name="test_private_udp_loopback",
        network_mode="private",
        expect_tokens=[
            "--- Testing private UDP loopback ---",
            "Server received",
            "Private UDP loopback test PASSED!",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

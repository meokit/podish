from pathlib import Path

from .harness import EmulatorCase, run_case


def test_ping_socket(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="ping_socket",
        binary_name="test_ping_socket",
        expect_tokens=[
            "--- Testing ICMP ping socket ---",
            "Created ping socket fd=",
            "Verified SO_TYPE=SOCK_DGRAM",
            "Sent ICMP echo request",
            "Received ICMP echo reply",
            "Ping socket test PASSED!",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

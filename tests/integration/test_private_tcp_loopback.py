from pathlib import Path

from .harness import EmulatorCase, run_case


def test_private_tcp_loopback(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="private_tcp_loopback",
        binary_name="test_private_tcp_loopback",
        network_mode="private",
        expect_tokens=[
            "--- Testing private TCP loopback ---",
            "Listening on 127.0.0.1:19090",
            "Client sent",
            "Server received",
            "Private TCP loopback test PASSED!",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

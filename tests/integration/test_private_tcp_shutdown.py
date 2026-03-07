from pathlib import Path

from .harness import EmulatorCase, run_case


def test_private_tcp_shutdown(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="private_tcp_shutdown",
        binary_name="test_private_tcp_shutdown",
        network_mode="private",
        expect_tokens=[
            "--- Testing private TCP shutdown semantics ---",
            "Received payload before EOF",
            "Observed revents=",
            "Observed EOF after peer shutdown",
            "Observed EPIPE after local shutdown",
            "Private TCP shutdown test PASSED!",
        ],
        timeout=30,
    )

    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

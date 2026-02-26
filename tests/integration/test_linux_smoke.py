from __future__ import annotations

from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case, run_interactive_case


@pytest.mark.integration
@pytest.mark.parametrize(
    "case",
    [
        EmulatorCase(
            name="hello_static",
            binary_name="hello_static",
            expect_tokens=["PASS: Hello World"],
        ),
        EmulatorCase(
            name="syscall_test",
            binary_name="syscall_test",
            rootfs=Path("tests/linux"),
            expect_tokens=["All Tests Passed!"],
        ),
        EmulatorCase(
            name="test_sigill",
            binary_name="test_sigill",
            expect_tokens=["PASS: Received SIGILL"],
        ),
        EmulatorCase(
            name="test_div0",
            binary_name="test_div0",
            expect_tokens=["PASS: Received SIGFPE"],
        ),
        EmulatorCase(
            name="test_segv",
            binary_name="test_segv",
            expect_tokens=["PASS: Received SIGSEGV"],
        ),
        EmulatorCase(
            name="test_fetch_fault",
            binary_name="test_fetch_fault",
            expect_tokens=["PASS: Received SIGSEGV at fetch"],
        ),
    ],
)
def test_linux_smoke_cases(
    project_root: Path,
    integration_assets_dir: Path,
    case: EmulatorCase,
    fiberpod_dll: str | None,
    alpine_image: str | None,
) -> None:
    """Run static linking smoke tests."""
    # In FiberPod mode, resolve rootfs path if needed
    if case.rootfs is not None:
        case.rootfs = (project_root / case.rootfs).resolve()

    run_case(
        project_root,
        integration_assets_dir,
        case,
        fiberpod_dll=fiberpod_dll,
        alpine_image=alpine_image,
    )


@pytest.mark.integration
def test_interactive_ash_echo(
    project_root: Path,
    fiberpod_dll: str | None,
) -> None:
    """Test interactive ash shell with echo command."""
    rootfs = project_root / "tests/linux/rootfs"
    ash = rootfs / "bin/ash"
    if not ash.exists():
        pytest.skip(f"missing ash in rootfs: {ash}")

    # Use FiberPod with rootfs path
    run_interactive_case(
        project_root=project_root,
        rootfs_or_image=str(rootfs),
        command="/bin/ash",
        fiberpod_dll=fiberpod_dll,
        expect_prompt=r"# ",
        test_commands=[("echo INTEGRATION_OK", "INTEGRATION_OK")],
    )

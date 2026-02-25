from __future__ import annotations

from pathlib import Path

import pexpect
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
    run_mode: str,
    fiberpod_dll: str | None,
    static_tests_image: str | None,
) -> None:
    """Run static linking smoke tests."""
    if run_mode == "fiberpod":
        # In FiberPod mode, use OCI image for static tests
        if case.rootfs is not None:
            # Tests that need rootfs use direct rootfs path
            case.rootfs = (project_root / case.rootfs).resolve()
        else:
            # Pure static tests use the static tests image
            case.image = static_tests_image
    else:
        # Legacy mode: resolve rootfs path
        if case.rootfs is not None:
            case.rootfs = (project_root / case.rootfs).resolve()

    run_case(
        project_root,
        integration_assets_dir,
        case,
        run_mode=run_mode,
        fiberpod_dll=fiberpod_dll,
    )


@pytest.mark.integration
def test_interactive_ash_echo(
    project_root: Path,
    run_mode: str,
    fiberpod_dll: str | None,
) -> None:
    """Test interactive ash shell with echo command."""
    rootfs = project_root / "tests/linux/rootfs"
    ash = rootfs / "bin/ash"
    if not ash.exists():
        pytest.skip(f"missing ash in rootfs: {ash}")

    if run_mode == "fiberpod":
        # Use FiberPod with rootfs path
        run_interactive_case(
            project_root=project_root,
            rootfs_or_image=str(rootfs),
            command="/bin/ash",
            fiberpod_dll=fiberpod_dll,
            expect_prompt=r"# ",
            test_commands=[("echo INTEGRATION_OK", "INTEGRATION_OK")],
        )
    else:
        # Legacy mode
        cli_project = project_root / "Fiberish.Cli" / "Fiberish.Cli.csproj"
        cmd = [
            "run",
            "--project",
            str(cli_project),
            "--no-build",
            "--",
            "--rootfs",
            str(rootfs),
            "/bin/ash",
        ]

        child = pexpect.spawn(
            "dotnet",
            cmd,
            cwd=str(project_root),
            encoding="utf-8",
            timeout=20,
        )
        try:
            child.expect(r"# ")
            child.sendline("echo INTEGRATION_OK")
            child.expect("INTEGRATION_OK")
            child.sendline("exit")
            child.expect(pexpect.EOF)
            child.close()
            assert child.exitstatus == 0
        finally:
            if child.isalive():
                child.terminate(force=True)

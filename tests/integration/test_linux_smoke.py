from __future__ import annotations

from pathlib import Path

import pexpect
import pytest

from .harness import EmulatorCase, run_case


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
    ],
)
def test_linux_smoke_cases(project_root: Path, integration_assets_dir: Path, case: EmulatorCase) -> None:
    if case.rootfs is not None:
        case.rootfs = (project_root / case.rootfs).resolve()
    run_case(project_root, integration_assets_dir, case)


@pytest.mark.integration
def test_interactive_ash_echo(project_root: Path) -> None:
    rootfs = project_root / "tests/linux/rootfs"
    ash = rootfs / "bin/ash"
    if not ash.exists():
        pytest.skip(f"missing ash in rootfs: {ash}")

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

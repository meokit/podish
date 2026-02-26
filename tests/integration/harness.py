from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Literal

import pexpect


@dataclass
class EmulatorCase:
    name: str
    binary_name: str
    args: list[str] = field(default_factory=list)
    rootfs: Path | None = None
    expect_tokens: list[str] = field(default_factory=list)
    timeout: int = 30
    send_eof: bool = False
    allow_timeout: bool = False


def _legacy_cmd(
    project_root: Path, test_bin: Path, rootfs: Path | None, args: Iterable[str]
) -> tuple[str, list[str]]:
    """Build command for Fiberish.Cli (legacy mode)."""
    cli_project = project_root / "Fiberish.Cli" / "Fiberish.Cli.csproj"
    cmd = [
        "run",
        "--project",
        str(cli_project),
        "--no-build",
        "--",
    ]
    if rootfs:
        cmd.extend(["--rootfs", str(rootfs)])
    cmd.append("--")
    cmd.append(str(test_bin))
    cmd.extend(args)
    return "dotnet", cmd


def _fiberpod_cmd(
    fiberpod_dll: str,
    image_or_rootfs: str,
    command: str,
    args: Iterable[str],
    use_tty: bool = False,
    volumes: list[tuple[str, str]] | None = None,
) -> tuple[str, list[str]]:
    """Build command for FiberPod."""
    cmd = [
        fiberpod_dll,
        "run",
    ]

    # Add volumes if specified
    if volumes:
        for host_path, guest_path in volumes:
            cmd.extend(["-v", f"{host_path}:{guest_path}"])

    # Add TTY flags for interactive tests
    if use_tty:
        cmd.extend(["-i", "-t"])

    # Add image/rootfs
    cmd.append(image_or_rootfs)

    # Add command and args
    if command:
        cmd.append(command)
    cmd.extend(args)

    return "dotnet", cmd


def run_case(
    project_root: Path,
    assets_dir: Path,
    case: EmulatorCase,
    run_mode: Literal["fiberpod", "legacy"] = "legacy",
    fiberpod_dll: str | None = None,
    alpine_image: str | None = None,
) -> str:
    """
    Run an emulator test case.

    Args:
        project_root: Repository root path
        assets_dir: Directory containing test binaries
        case: Test case configuration
        run_mode: 'fiberpod' for FiberPod CLI, 'legacy' for Fiberish.Cli
        fiberpod_dll: Path to FiberPod.dll (required for fiberpod mode)
        alpine_image: Alpine image name (required for fiberpod mode without rootfs)

    Returns:
        The output from the emulator
    """
    if run_mode == "fiberpod":
        return _run_case_fiberpod(project_root, case, fiberpod_dll, assets_dir, alpine_image)
    else:
        return _run_case_legacy(project_root, assets_dir, case)


def _run_case_legacy(project_root: Path, assets_dir: Path, case: EmulatorCase) -> str:
    """Run a test case using Fiberish.Cli (legacy mode)."""
    test_bin = assets_dir / case.binary_name
    if not test_bin.exists():
        raise AssertionError(f"Missing integration binary: {test_bin}")

    dotnet, args = _legacy_cmd(project_root, test_bin, case.rootfs, case.args)

    child = pexpect.spawn(
        dotnet,
        args,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=case.timeout,
    )

    return _wait_and_validate(child, case)


def _run_case_fiberpod(
    project_root: Path,
    case: EmulatorCase,
    fiberpod_dll: str | None,
    assets_dir: Path | None,
    alpine_image: str | None,
) -> str:
    """Run a test case using FiberPod."""
    if fiberpod_dll is None:
        raise ValueError("fiberpod_dll is required for fiberpod mode")
    if assets_dir is None:
        raise ValueError("assets_dir is required for fiberpod mode")
    if not assets_dir.exists():
        raise AssertionError(f"Assets directory not found: {assets_dir}")

    # Determine image or rootfs to use
    if case.rootfs:
        # Rootfs mode - use custom rootfs
        image_or_rootfs = str(case.rootfs)
    elif alpine_image:
        # Alpine image mode - tests are mounted via -v
        image_or_rootfs = alpine_image
    else:
        raise ValueError("Either alpine_image or case.rootfs must be set for FiberPod mode")

    # Always mount assets directory to /tests
    volumes = [(str(assets_dir), "/tests")]
    command = f"/tests/{case.binary_name}"

    dotnet, args = _fiberpod_cmd(
        fiberpod_dll=fiberpod_dll,
        image_or_rootfs=image_or_rootfs,
        command=command,
        args=case.args,
        use_tty=False,
        volumes=volumes,
    )

    child = pexpect.spawn(
        dotnet,
        args,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=case.timeout,
    )

    return _wait_and_validate(child, case)


def _wait_and_validate(child: pexpect.spawn, case: EmulatorCase) -> str:
    """Wait for child process and validate output."""
    if case.send_eof:
        child.sendeof()

    timed_out = False
    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        if not case.allow_timeout:
            raise
        timed_out = True

    output = child.before or ""

    if timed_out:
        child.close(force=True)
    else:
        child.close()

    if not timed_out and child.exitstatus != 0:
        raise AssertionError(
            f"[{case.name}] emulator exited with {child.exitstatus}\nOutput:\n{output}"
        )

    for token in case.expect_tokens:
        if token not in output:
            raise AssertionError(
                f"[{case.name}] missing token {token!r}\nOutput:\n{output}"
            )

    return output


def run_interactive_case(
    project_root: Path,
    rootfs_or_image: str,
    command: str,
    fiberpod_dll: str,
    expect_prompt: str = r"# ",
    test_commands: list[tuple[str, str]] | None = None,
    timeout: int = 30,
) -> str:
    """
    Run an interactive test case with FiberPod.

    Args:
        project_root: Repository root path
        rootfs_or_image: Rootfs path or OCI image name
        command: Command to run in the container
        fiberpod_dll: Path to FiberPod.dll
        expect_prompt: Prompt pattern to expect
        test_commands: List of (command, expected_output) tuples
        timeout: Timeout in seconds

    Returns:
        The output from the emulator
    """
    dotnet, args = _fiberpod_cmd(
        fiberpod_dll=fiberpod_dll,
        image_or_rootfs=rootfs_or_image,
        command=command,
        args=[],
        use_tty=True,
    )

    child = pexpect.spawn(
        dotnet,
        args,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=timeout,
    )

    try:
        child.expect(expect_prompt)
        output = child.before or ""

        if test_commands:
            for cmd, expected in test_commands:
                child.sendline(cmd)
                if expected:
                    child.expect(expected)
                    output += child.before or ""

        return output
    finally:
        if child.isalive():
            child.terminate(force=True)

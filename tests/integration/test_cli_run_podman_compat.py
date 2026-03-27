"""Integration tests for podman-compatible `run` argument parsing."""
from pathlib import Path

import pexpect


def test_run_allows_guest_args_after_image_without_explicit_separator(
    project_root: Path,
    alpine_image: str,
) -> None:
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", "54326:12350",
        "--init",
        alpine_image,
        "/bin/sh", "-c", "echo podman-compat",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect_exact("podman-compat", timeout=20)
        child.expect(pexpect.EOF, timeout=20)
    except pexpect.exceptions.TIMEOUT as exc:
        output = child.before or ""
        child.close(force=True)
        raise AssertionError(f"Command timed out. Output:\n{output}") from exc

    output = child.before or ""
    child.close()

    assert child.exitstatus == 0, f"Expected exit code 0, got {child.exitstatus}. Output:\n{output}"

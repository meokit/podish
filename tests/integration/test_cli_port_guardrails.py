"""Tests for CLI network-mode guardrails and port validation."""
from pathlib import Path

import pexpect
import pytest

from .harness import EmulatorCase, _wait_and_validate


def test_run_host_network_with_publish_rejected(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    """Test that `run --network host -p ...` exits with 125."""
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "host",
        "-p", "8080:80",
        alpine_image,
        "echo",
        "should not run",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)
        raise AssertionError("Command timed out")

    output = child.before or ""
    child.close()

    # Should exit with 125
    assert child.exitstatus == 125, f"Expected exit code 125, got {child.exitstatus}. Output:\n{output}"
    # Should have error message about --publish not supported in host mode
    assert "not supported in host network mode" in output.lower() or \
           "--publish" in output, f"Expected error about --publish in host mode. Output:\n{output}"


def test_run_private_network_invalid_port_zero(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    """Test that invalid published ports like `0:80` are rejected."""
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", "0:80",
        alpine_image,
        "echo",
        "should not run",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)
        raise AssertionError("Command timed out")

    output = child.before or ""
    child.close()

    # Should exit with 125
    assert child.exitstatus == 125, f"Expected exit code 125, got {child.exitstatus}. Output:\n{output}"
    # Should have error message about invalid port
    assert "invalid published port" in output.lower() or \
           "invalid" in output.lower(), f"Expected error about invalid port. Output:\n{output}"


def test_run_private_network_invalid_port_too_high(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    """Test that invalid published ports like `8080:70000` are rejected."""
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", "8080:70000",
        alpine_image,
        "echo",
        "should not run",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)
        raise AssertionError("Command timed out")

    output = child.before or ""
    child.close()

    # Should exit with 125
    assert child.exitstatus == 125, f"Expected exit code 125, got {child.exitstatus}. Output:\n{output}"


def test_run_private_network_invalid_port_negative(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    """Test that invalid published ports like negative values are rejected."""
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", "-1:80",
        alpine_image,
        "echo",
        "should not run",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)
        raise AssertionError("Command timed out")

    output = child.before or ""
    child.close()

    # Should exit with 125
    assert child.exitstatus == 125, f"Expected exit code 125, got {child.exitstatus}. Output:\n{output}"


def test_run_private_network_invalid_format_missing_colon(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    """Test that invalid published port format (missing colon) is rejected."""
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", "8080",
        alpine_image,
        "echo",
        "should not run",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)
        raise AssertionError("Command timed out")

    output = child.before or ""
    child.close()

    # Should exit with 125
    assert child.exitstatus == 125, f"Expected exit code 125, got {child.exitstatus}. Output:\n{output}"


def test_run_private_network_invalid_format_extra_colon(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    """Test that invalid published port format (extra colon) is rejected."""
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", "8080:80:tcp",
        alpine_image,
        "echo",
        "should not run",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)
        raise AssertionError("Command timed out")

    output = child.before or ""
    child.close()

    # Should exit with 125
    assert child.exitstatus == 125, f"Expected exit code 125, got {child.exitstatus}. Output:\n{output}"

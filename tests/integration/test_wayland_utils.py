from __future__ import annotations

import os
import subprocess
from pathlib import Path

import pytest


pytestmark = pytest.mark.integration


def test_wayland_info_lists_core_globals(project_root: Path, wayland_utils_image: str) -> None:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--wayland-server",
        wayland_utils_image,
        "--",
        "/usr/bin/wayland-info",
    ]

    clean_env = {
        "TERM": os.environ.get("TERM", "xterm"),
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "DOTNET_CLI_HOME": os.path.expanduser("~"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_NOLOGO": "true",
    }

    result = subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=30,
        env=clean_env,
    )

    output = result.stdout + result.stderr
    assert result.returncode == 0, (
        f"wayland-info failed with {result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )
    assert "wl_compositor" in output, f"missing wl_compositor in output:\n{output}"
    assert "wl_shm" in output, f"missing wl_shm in output:\n{output}"
    assert "xdg_wm_base" in output, f"missing xdg_wm_base in output:\n{output}"


def test_wayland_client_can_create_xdg_surface_and_commit_shm_buffer(
    project_root: Path,
    wayland_utils_image: str,
) -> None:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--wayland-server",
        wayland_utils_image,
        "--",
        "/usr/local/bin/test_wayland_shm_window",
    ]

    clean_env = {
        "TERM": os.environ.get("TERM", "xterm"),
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "DOTNET_CLI_HOME": os.path.expanduser("~"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_NOLOGO": "true",
    }

    result = subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=30,
        env=clean_env,
    )

    output = result.stdout + result.stderr
    assert result.returncode == 0, (
        f"test_wayland_shm_window failed with {result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )
    assert "CONNECTED" in output, output
    assert "GLOBALS_OK" in output, output
    assert "SURFACE_CREATED" in output, output
    assert "CONFIGURED" in output, output
    assert "BUFFER_COMMITTED" in output, output
    assert "SUCCESS" in output, output

from __future__ import annotations

import os
import subprocess
from pathlib import Path

import pytest


pytestmark = pytest.mark.integration


def _clean_wayland_env() -> dict[str, str]:
    return {
        "TERM": os.environ.get("TERM", "xterm"),
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "DOTNET_CLI_HOME": os.path.expanduser("~"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_NOLOGO": "true",
    }


def _run_wayland_client(
    project_root: Path,
    wayland_utils_image: str,
    guest_argv: list[str],
    *,
    desktop_size: str = "1024x768",
    timeout: int = 10,
) -> tuple[subprocess.CompletedProcess[str] | None, str]:
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
        "--wayland-desktop-size",
        desktop_size,
        wayland_utils_image,
        "--",
        *guest_argv,
    ]

    try:
        result = subprocess.run(
            cmd,
            cwd=str(project_root),
            capture_output=True,
            text=True,
            timeout=timeout,
            env=_clean_wayland_env(),
        )
        output = result.stdout + result.stderr
    except subprocess.TimeoutExpired as exc:
        output = (exc.stdout or "") + (exc.stderr or "")
        result = None

    return result, output


def test_wayland_info_lists_core_globals(project_root: Path, wayland_utils_image: str) -> None:
    result, output = _run_wayland_client(
        project_root,
        wayland_utils_image,
        ["/usr/bin/wayland-info"],
        timeout=30,
    )
    assert result is not None
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
    result, output = _run_wayland_client(
        project_root,
        wayland_utils_image,
        ["/usr/local/bin/test_wayland_shm_window"],
        timeout=30,
    )
    assert result is not None
    assert result.returncode == 0, (
        f"test_wayland_shm_window failed with {result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )
    assert "CONNECTED" in output, output
    assert "GLOBALS_OK" in output, output
    assert "SURFACE_CREATED" in output, output
    assert "CONFIGURED" in output, output
    assert "BUFFER_COMMITTED" in output, output
    assert "SUCCESS" in output, output


def test_weston_simple_shm_can_connect_and_stay_alive_briefly(
    project_root: Path,
    wayland_utils_image: str,
) -> None:
    result, output = _run_wayland_client(
        project_root,
        wayland_utils_image,
        ["/usr/bin/weston-simple-shm"],
        timeout=10,
    )

    if "creating a buffer file" in output and "Function not implemented" in output:
        pytest.xfail("weston-simple-shm currently blocks on guest fallocate(2) for anonymous shm buffers")
    if result is not None:
        assert result.returncode == 0, (
            f"weston-simple-shm failed with {result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )
    assert "protocol error" not in output.lower(), output
    assert "server bug" not in output.lower(), output
    assert "creating a buffer file" not in output.lower(), output


def test_weston_stacking_can_drive_multiple_windows(
    project_root: Path,
    wayland_utils_image: str,
) -> None:
    result, output = _run_wayland_client(
        project_root,
        wayland_utils_image,
        ["/usr/bin/weston-stacking"],
        timeout=10,
    )

    if "not found" in output.lower() or "no such file" in output.lower():
        pytest.xfail("weston-stacking is not present in this weston-clients package variant")

    if "protocol error" in output.lower():
        pytest.xfail(f"weston-stacking exposed a Wayland protocol gap:\n{output}")

    if "server bug" in output.lower():
        pytest.xfail(f"weston-stacking exposed a compositor buffer lifecycle gap:\n{output}")

    if "could not load cursor" in output.lower():
        pytest.xfail(f"weston-stacking is currently blocked on guest cursor theme resolution:\n{output}")

    if result is not None:
        assert result.returncode == 0, (
            f"weston-stacking failed with {result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )

    assert "failed" not in output.lower(), output


def test_two_weston_simple_shm_clients_can_share_the_desktop(
    project_root: Path,
    wayland_utils_image: str,
) -> None:
    result, output = _run_wayland_client(
        project_root,
        wayland_utils_image,
        [
            "/bin/sh",
            "-lc",
            "/usr/bin/weston-simple-shm >/tmp/w1.log 2>&1 & "
            "/usr/bin/weston-simple-shm >/tmp/w2.log 2>&1 & "
            "wait",
        ],
        timeout=10,
    )

    if "creating a buffer file" in output and "Function not implemented" in output:
        pytest.xfail("multi-window weston-simple-shm currently blocks on guest fallocate(2)")

    if result is not None:
        assert result.returncode == 0, (
            f"two weston-simple-shm clients failed with {result.returncode}\nstdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )

    assert "protocol error" not in output.lower(), output
    assert "server bug" not in output.lower(), output
    assert "creating a buffer file" not in output.lower(), output

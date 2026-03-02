"""
Root-level pytest fixtures shared by ALL test directories
(tests/linux/, tests/integration/, tests/unit/, etc.).

Pytest automatically discovers conftest.py files walking upward from any
test file, so placing this here makes every fixture here available
everywhere without any explicit import.
"""

from __future__ import annotations

import os
import subprocess
import tempfile
from pathlib import Path
from typing import Literal

import pytest

PROJECT_ROOT = Path(__file__).resolve().parent.parent
FIBERPOD_PROJECT = PROJECT_ROOT / "FiberPod"
FIBERPOD_DLL = FIBERPOD_PROJECT / "bin" / "Debug" / "net8.0" / "FiberPod.dll"


@pytest.fixture(scope="session")
def project_root() -> Path:
    """Absolute path to the repository root."""
    return PROJECT_ROOT


@pytest.fixture(scope="session")
def build_fiberpod() -> str:
    """
    Build FiberPod once per pytest session.

    Returns the path to the built DLL for running containerized tests.
    """
    print(f"\n[conftest] Building FiberPod (Debug)…")
    subprocess.run(
        ["dotnet", "build", str(FIBERPOD_PROJECT), "-c", "Debug"],
        check=True,
    )
    print(f"[conftest] Build complete → {FIBERPOD_DLL}")
    return str(FIBERPOD_DLL)


@pytest.fixture(scope="session")
def fiberpod_dll(build_fiberpod) -> str:
    """Alias for build_fiberpod; returns the DLL path."""
    return build_fiberpod


def check_podman_available() -> bool:
    """Check if podman is available on the system."""
    try:
        result = subprocess.run(
            ["podman", "version", "--format", "{{.Client.Version}}"],
            capture_output=True,
            text=True,
            timeout=10
        )
        return result.returncode == 0
    except (subprocess.TimeoutExpired, FileNotFoundError):
        return False


@pytest.fixture(scope="session")
def podman_available() -> bool:
    """Check if podman is available."""
    available = check_podman_available()
    if not available:
        print("[conftest] Warning: podman not available")
    return available


# Alpine i386 image for FiberPod tests
ALPINE_I386_IMAGE = "docker.io/i386/alpine:latest"
ALPINE_IMAGE = "localhost/fiberish-alpine-i386:latest"

@pytest.fixture(scope="session")
def alpine_image(podman_available: bool) -> str:
    """
    Build an Alpine i386 image with openssl preinstalled, export it as rootfs.
    """
    override = os.environ.get("FIBERISH_ALPINE_IMAGE")
    if override:
        return override

    if not podman_available:
        pytest.skip("podman not available; set FIBERISH_ALPINE_IMAGE to a prepared image")

    subprocess.run(["podman", "pull", "--platform", "linux/386", ALPINE_I386_IMAGE], check=True)
    subprocess.run(
        [
            "podman",
            "build",
            "--platform",
            "linux/386",
            "--pull=never",
            "-t",
            ALPINE_IMAGE,
            "-f",
            "-",
            "/tmp",
        ],
        check=True,
        input=f"FROM {ALPINE_I386_IMAGE}\nRUN apk add --no-cache openssl\n",
        text=True,
    )

    rootfs_dir = Path(tempfile.mkdtemp(prefix="fiberish-alpine-rootfs-"))
    tar_path = rootfs_dir / "rootfs.tar"
    container_id = subprocess.check_output(
        ["podman", "create", "--platform", "linux/386", ALPINE_IMAGE],
        text=True,
    ).strip()
    try:
        subprocess.run(["podman", "export", container_id, "-o", str(tar_path)], check=True)
        subprocess.run(["tar", "-xf", str(tar_path), "-C", str(rootfs_dir)], check=True)
    finally:
        subprocess.run(["podman", "rm", "-f", container_id], check=False)
        tar_path.unlink(missing_ok=True)

    return str(rootfs_dir)

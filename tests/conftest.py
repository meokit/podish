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
from pathlib import Path
from typing import Literal

import pytest

PROJECT_ROOT = Path(__file__).resolve().parent.parent
FIBERPOD_PROJECT = PROJECT_ROOT / "Podish.Cli"
FIBERPOD_DLL = FIBERPOD_PROJECT / "bin" / "Debug" / "net8.0" / "Podish.Cli.dll"


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


# Alpine i386 image for FiberPod tests (OCI store mode)
ALPINE_OCI_IMAGE = "docker.io/i386/alpine:latest"

@pytest.fixture(scope="session")
def alpine_image(build_fiberpod: str) -> str:
    """
    Ensure Alpine i386 image is available in FiberPod OCI store and return store path.
    """
    override = os.environ.get("FIBERISH_ALPINE_IMAGE")
    if override:
        return override

    safe_image = ALPINE_OCI_IMAGE.replace("/", "_").replace(":", "_")
    store_dir = PROJECT_ROOT / ".fiberpod" / "oci" / "images" / safe_image
    image_meta = store_dir / "image.json"
    if not image_meta.exists():
        print(f"\n[conftest] Pulling OCI store image {ALPINE_OCI_IMAGE} for integration tests…")
        subprocess.run(
            [
                "dotnet",
                "run",
                "--project",
                str(FIBERPOD_PROJECT),
                "--no-build",
                "--",
                "pull",
                "--store-oci",
                ALPINE_OCI_IMAGE,
            ],
            check=True,
            cwd=str(PROJECT_ROOT),
        )
    return str(store_dir)

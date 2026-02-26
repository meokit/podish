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

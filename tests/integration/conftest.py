"""
Fixtures for tests/integration/.

Session-scoped fixtures `project_root`, `build_cli`, `cli_dll`,
`build_fiberpod`, `fiberpod_dll`, and `podman_available` are
inherited from tests/conftest.py — no need to redefine them here.
"""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

import pytest


DEFAULT_BUILD_DIR = Path(__file__).resolve().parents[2] / "build" / "integration-assets"
DEFAULT_ASSETS_CANDIDATES = (
    DEFAULT_BUILD_DIR / "assets",
    DEFAULT_BUILD_DIR / "tests" / "integration" / "assets",
)

# Alpine i386 image for FiberPod tests
ALPINE_I386_IMAGE = "docker.io/i386/alpine:latest"


@pytest.fixture(scope="session")
def integration_assets_dir(project_root) -> Path:
    """
    Resolves the directory containing compiled integration test binaries.
    Auto-builds with CMake if not found.
    """
    override = os.environ.get("FIBERISH_INTEGRATION_ASSETS_DIR")
    if override:
        assets_dir = Path(override).resolve()
    else:
        assets_dir = next(
            (p for p in DEFAULT_ASSETS_CANDIDATES if p.exists()),
            DEFAULT_ASSETS_CANDIDATES[0],
        )

    if not assets_dir.exists():
        # Auto-build with CMake
        print(f"\n[conftest] Integration assets not found, building with CMake...")
        build_dir = project_root / "build" / "integration-assets"
        subprocess.run(
            [
                "cmake", "-S", str(project_root / "tests/integration"),
                "-B", str(build_dir),
                f"-DFIBERISH_PROJECT_ROOT={project_root}"
            ],
            check=True,
            capture_output=True,
        )
        subprocess.run(
            ["cmake", "--build", str(build_dir), "--target", "integration-tests-build"],
            check=True,
        )
        print(f"[conftest] Test binaries built in {assets_dir}")

    return assets_dir


@pytest.fixture(scope="session")
def alpine_image() -> str | None:
    """
    Return the Alpine i386 image name for FiberPod tests.
    FiberPod will auto-pull the image on first run if needed.
    """
    return ALPINE_I386_IMAGE

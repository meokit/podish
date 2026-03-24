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
WAYLAND_UTILS_IMAGE = "localhost/podish-wayland-utils:alpine"


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

    build_dir = project_root / "build" / "integration-assets"

    if not assets_dir.exists():
        print(f"\n[conftest] Integration assets not found, configuring CMake...")
        subprocess.run(
            [
                "cmake", "-S", str(project_root / "tests/integration"),
                "-B", str(build_dir),
                f"-DFIBERISH_PROJECT_ROOT={project_root}"
            ],
            check=True,
            capture_output=True,
        )

    # Always build once per test session so newly added assets are picked up.
    subprocess.run(
        ["cmake", "--build", str(build_dir), "--target", "integration-tests-build"],
        check=True,
    )
    print(f"[conftest] Test binaries built in {assets_dir}")

    return assets_dir


@pytest.fixture(scope="session")
def wayland_utils_image(project_root: Path, build_fiberpod: str, podman_available: bool, tmp_path_factory) -> str:
    """
    Build an Alpine OCI image with wayland-utils via podman and load it into Podish's OCI store.
    """
    _ = build_fiberpod
    if not podman_available:
        pytest.skip("podman is required for wayland integration tests")

    containerfile = project_root / "tests" / "integration" / "Containerfile.wayland-utils"
    archive_dir = tmp_path_factory.mktemp("wayland-image")
    archive_path = archive_dir / "wayland-utils.oci.tar"

    subprocess.run(
        [
            "podman",
            "build",
            "--arch",
            "386",
            "-f",
            str(containerfile),
            "-t",
            WAYLAND_UTILS_IMAGE,
            str(project_root),
        ],
        check=True,
        timeout=300,
    )

    subprocess.run(
        [
            "podman",
            "save",
            "--format",
            "oci-archive",
            "-o",
            str(archive_path),
            WAYLAND_UTILS_IMAGE,
        ],
        check=True,
        timeout=120,
    )

    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
            "--no-build",
            "--",
            "load",
            "-i",
            str(archive_path),
        ],
        check=True,
        cwd=str(project_root),
        timeout=120,
    )

    return WAYLAND_UTILS_IMAGE

"""
Fixtures for tests/integration/.

Session-scoped fixtures `project_root`, `build_cli`, `cli_dll`,
`build_fiberpod`, `fiberpod_dll`, and `podman_available` are
inherited from tests/conftest.py — no need to redefine them here.
"""

from __future__ import annotations

import os
import subprocess
import tempfile
from pathlib import Path

import pytest


DEFAULT_BUILD_DIR = Path(__file__).resolve().parents[2] / "build" / "integration-assets"
DEFAULT_ASSETS_CANDIDATES = (
    DEFAULT_BUILD_DIR / "assets",
    DEFAULT_BUILD_DIR / "tests" / "integration" / "assets",
)

# Alpine i386 image for FiberPod tests
ALPINE_I386_IMAGE = "docker.io/i386/alpine:latest"
OPENSSL_ALPINE_IMAGE = "localhost/fiberish-openssl-alpine-i386:latest"


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
def alpine_image(podman_available: bool) -> str:
    """
    Export an Alpine i386 image as rootfs using podman.
    """
    override = os.environ.get("FIBERISH_ALPINE_IMAGE")
    if override:
        return override

    if not podman_available:
        pytest.skip("podman not available; set FIBERISH_ALPINE_IMAGE to a prepared image or rootfs")

    subprocess.run(["podman", "pull", "--platform", "linux/386", ALPINE_I386_IMAGE], check=True)

    rootfs_dir = Path(tempfile.mkdtemp(prefix="fiberish-alpine-rootfs-"))
    tar_path = rootfs_dir / "rootfs.tar"
    container_id = subprocess.check_output(
        ["podman", "create", "--platform", "linux/386", ALPINE_I386_IMAGE],
        text=True,
    ).strip()
    try:
        subprocess.run(["podman", "export", container_id, "-o", str(tar_path)], check=True)
        subprocess.run(["tar", "-xf", str(tar_path), "-C", str(rootfs_dir)], check=True)
    finally:
        subprocess.run(["podman", "rm", "-f", container_id], check=False)
        tar_path.unlink(missing_ok=True)

    return str(rootfs_dir)


@pytest.fixture(scope="session")
def openssl_alpine_image(podman_available: bool) -> str:
    """
    Build an Alpine i386 image with openssl preinstalled, export it as rootfs.
    """
    override = os.environ.get("FIBERISH_OPENSSL_IMAGE")
    if override:
        return override

    if not podman_available:
        pytest.skip("podman not available; set FIBERISH_OPENSSL_IMAGE to a prepared image")

    subprocess.run(["podman", "pull", "--platform", "linux/386", ALPINE_I386_IMAGE], check=True)
    subprocess.run(
        [
            "podman",
            "build",
            "--platform",
            "linux/386",
            "--pull=never",
            "-t",
            OPENSSL_ALPINE_IMAGE,
            "-f",
            "-",
            "/tmp",
        ],
        check=True,
        input=f"FROM {ALPINE_I386_IMAGE}\nRUN apk add --no-cache openssl\n",
        text=True,
    )

    rootfs_dir = Path(tempfile.mkdtemp(prefix="fiberish-openssl-rootfs-"))
    tar_path = rootfs_dir / "rootfs.tar"
    container_id = subprocess.check_output(
        ["podman", "create", "--platform", "linux/386", OPENSSL_ALPINE_IMAGE],
        text=True,
    ).strip()
    try:
        subprocess.run(["podman", "export", container_id, "-o", str(tar_path)], check=True)
        subprocess.run(["tar", "-xf", str(tar_path), "-C", str(rootfs_dir)], check=True)
    finally:
        subprocess.run(["podman", "rm", "-f", container_id], check=False)
        tar_path.unlink(missing_ok=True)

    return str(rootfs_dir)

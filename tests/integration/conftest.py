"""
Fixtures for tests/integration/.

Session-scoped fixtures `project_root`, `build_cli`, `cli_dll`,
`build_fiberpod`, `fiberpod_dll`, `run_mode`, and `podman_available` are
inherited from tests/conftest.py — no need to redefine them here.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path

import pytest


DEFAULT_BUILD_DIR = Path(__file__).resolve().parents[2] / "build" / "integration-assets"
DEFAULT_ASSETS_CANDIDATES = (
    DEFAULT_BUILD_DIR / "assets",
    DEFAULT_BUILD_DIR / "tests" / "integration" / "assets",
)

# Image name constants
IMAGE_STATIC_TESTS = "localhost/fiberish-tests:static"


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


def _get_image_id(image_name: str) -> str | None:
    """Get the ID of a local image."""
    try:
        result = subprocess.run(
            ["podman", "inspect", image_name, "--format", "{{.Id}}"],
            capture_output=True,
            text=True,
            timeout=30
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError):
        pass
    return None


def _check_fiberpod_image(project_root: Path, image_name: str) -> bool:
    """Check if image is exported to FiberPod images directory."""
    safe_name = image_name.replace("/", "_").replace(":", "_")
    export_dir = project_root / ".fiberpod" / "images" / safe_name
    return export_dir.exists() and (export_dir / "tests").exists()


def _build_and_export_image(
    project_root: Path,
    containerfile: str,
    image_name: str,
    assets_dir: Path,
) -> bool:
    """Build a container image and export to FiberPod images directory."""
    containerfile_path = project_root / "tests/integration" / containerfile
    if not containerfile_path.exists():
        raise FileNotFoundError(f"Containerfile not found: {containerfile_path}")

    print(f"\n[conftest] Building image {image_name}...")
    result = subprocess.run(
        [
            "podman", "build",
            "--arch=386",
            "-f", str(containerfile_path),
            "-t", image_name,
            str(project_root)
        ],
        capture_output=True,
        text=True,
        timeout=300  # 5 minutes
    )

    if result.returncode != 0:
        print(f"[conftest] Build failed:\n{result.stderr}")
        return False

    print(f"[conftest] Image {image_name} built successfully")

    # Export to FiberPod images directory
    safe_name = image_name.replace("/", "_").replace(":", "_")
    export_dir = project_root / ".fiberpod" / "images" / safe_name
    export_dir.mkdir(parents=True, exist_ok=True)

    print(f"[conftest] Exporting to {export_dir}...")

    # Create temporary container and export
    result = subprocess.run(
        ["podman", "create", image_name, "/bin/true"],
        capture_output=True,
        text=True,
        timeout=30
    )
    if result.returncode != 0:
        print(f"[conftest] Failed to create container: {result.stderr}")
        return False

    container_id = result.stdout.strip()

    try:
        # Export filesystem
        result = subprocess.run(
            ["podman", "export", container_id],
            capture_output=False,
            stdout=subprocess.PIPE,
            timeout=60
        )

        # Extract tar to export directory
        import tarfile
        import io
        tar_buffer = io.BytesIO(result.stdout)
        with tarfile.open(fileobj=tar_buffer, mode='r') as tar:
            tar.extractall(export_dir, filter='data')

    finally:
        # Remove temporary container
        subprocess.run(["podman", "rm", container_id], capture_output=True)

    print(f"[conftest] Exported to {export_dir}")
    return True


def _ensure_image(
    project_root: Path,
    containerfile: str,
    image_name: str,
    assets_dir: Path,
    force_rebuild: bool = False
) -> str:
    """Ensure an image exists and is exported, building if necessary."""
    # Check if rebuild is forced
    rebuild_env = os.environ.get("FIBERISH_REBUILD_IMAGES", "").lower()
    force_rebuild = force_rebuild or rebuild_env in ("1", "true", "yes")

    # Check if image is already exported to FiberPod
    if not force_rebuild and _check_fiberpod_image(project_root, image_name):
        print(f"[conftest] Image {image_name} already exported")
        return image_name

    # Build and export the image
    if not _build_and_export_image(project_root, containerfile, image_name, assets_dir):
        raise RuntimeError(f"Failed to build image: {image_name}")

    return image_name


@pytest.fixture(scope="session")
def static_tests_image(
    project_root: Path,
    podman_available: bool,
    run_mode: str,
    integration_assets_dir: Path
) -> str | None:
    """
    Ensure the static tests image is built and exported.
    Returns None if not in fiberpod mode or podman is unavailable.
    """
    if run_mode != "fiberpod":
        return None
    if not podman_available:
        pytest.skip("podman not available")
    return _ensure_image(
        project_root,
        "Containerfile.static-tests",
        IMAGE_STATIC_TESTS,
        integration_assets_dir
    )


@pytest.fixture(scope="session")
def fiberpod_images_dir(project_root: Path) -> Path:
    """Get the FiberPod images directory."""
    images_dir = project_root / ".fiberpod" / "images"
    images_dir.mkdir(parents=True, exist_ok=True)
    return images_dir

"""
Fixtures for tests/integration/.

Session-scoped fixtures `project_root`, `build_cli`, and `cli_dll` are
inherited from tests/conftest.py — no need to redefine them here.
"""

from __future__ import annotations

import os
from pathlib import Path

import pytest


DEFAULT_BUILD_DIR = Path(__file__).resolve().parents[2] / "build" / "integration-assets"
DEFAULT_ASSETS_CANDIDATES = (
    DEFAULT_BUILD_DIR / "assets",
    DEFAULT_BUILD_DIR / "tests" / "integration" / "assets",
)


@pytest.fixture(scope="session")
def integration_assets_dir(project_root) -> Path:
    """
    Resolves the directory containing compiled integration test binaries.

    Override with the FIBERISH_INTEGRATION_ASSETS_DIR environment variable,
    or let it auto-discover under build/integration-assets/.
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
        raise RuntimeError(
            f"Integration assets dir not found: {assets_dir}\n"
            f"Run: cmake -S {project_root / 'tests/integration'} -B {DEFAULT_BUILD_DIR} "
            f"-DFIBERISH_PROJECT_ROOT={project_root} && "
            f"cmake --build {DEFAULT_BUILD_DIR} --target integration-tests-build"
        )
    return assets_dir

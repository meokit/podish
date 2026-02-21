from __future__ import annotations

import os
from pathlib import Path

import pytest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_BUILD_DIR = PROJECT_ROOT / "build" / "integration-assets"
DEFAULT_ASSETS_CANDIDATES = (
    DEFAULT_BUILD_DIR / "assets",
    DEFAULT_BUILD_DIR / "tests" / "integration" / "assets",
)


@pytest.fixture(scope="session")
def project_root() -> Path:
    return PROJECT_ROOT


@pytest.fixture(scope="session")
def integration_assets_dir() -> Path:
    override = os.environ.get("FIBERISH_INTEGRATION_ASSETS_DIR")
    if override:
        assets_dir = Path(override).resolve()
    else:
        assets_dir = next((p for p in DEFAULT_ASSETS_CANDIDATES if p.exists()), DEFAULT_ASSETS_CANDIDATES[0])

    if not assets_dir.exists():
        raise RuntimeError(
            f"Integration assets dir not found: {assets_dir}\n"
            f"Run: cmake -S {PROJECT_ROOT / 'tests/integration'} -B {DEFAULT_BUILD_DIR} "
            f"-DFIBERISH_PROJECT_ROOT={PROJECT_ROOT} && "
            f"cmake --build {DEFAULT_BUILD_DIR} --target integration-tests-build"
        )
    return assets_dir

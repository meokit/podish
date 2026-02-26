"""Integration test for vfork semantics."""
from __future__ import annotations

from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case


@pytest.mark.integration
@pytest.mark.parametrize(
    "case",
    [
        EmulatorCase(
            name="test_vfork",
            binary_name="test_vfork",
            expect_tokens=["PASS: All VFork Tests"],
            timeout=30,
        ),
    ],
)
def test_vfork(project_root: Path, integration_assets_dir: Path, case: EmulatorCase, fiberpod_dll: str, alpine_image: str) -> None:
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

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

import pytest

PROJECT_ROOT = Path(__file__).resolve().parent.parent
CLI_PROJECT = PROJECT_ROOT / "Fiberish.Cli"
CLI_DLL = CLI_PROJECT / "bin" / "Debug" / "net8.0" / "Fiberish.Cli.dll"


@pytest.fixture(scope="session")
def project_root() -> Path:
    """Absolute path to the repository root."""
    return PROJECT_ROOT


@pytest.fixture(scope="session")
def build_cli() -> str:
    """
    Build Fiberish.Cli once per pytest session.

    Returns the path to the built DLL so tests can run it directly with
    `dotnet <dll>` instead of `dotnet run --project …`, avoiding the
    per-invocation build check that adds several seconds to each test.

    Usage in a test::

        def test_foo(build_cli):
            child = pexpect.spawn(f"dotnet {build_cli} my_asset")
    """
    print(f"\n[conftest] Building Fiberish.Cli (Debug)…")
    subprocess.run(
        ["dotnet", "build", str(CLI_PROJECT), "-c", "Debug"],
        check=True,
    )
    print(f"[conftest] Build complete → {CLI_DLL}")
    return str(CLI_DLL)


@pytest.fixture(scope="session")
def cli_dll(build_cli) -> str:
    """Alias for build_cli; returns the DLL path (build_cli also builds)."""
    return build_cli

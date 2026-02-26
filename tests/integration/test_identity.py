from __future__ import annotations

from pathlib import Path
import pytest
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_identity_syscalls(project_root: Path, fiberpod_dll: str, alpine_image: str) -> None:
    # This test runs both as root (default for emulator usually) or user.
    # The output differs slightly.
    # Since we don't easily control if the emulator runs as root or not from here (it depends on host user usually, 
    # unless we pass flags to emulator), we should expect at least the common parts.
    
    # "setgroups succeeded (root)" or "setgroups failed" depends on permission.
    # But usually in CI/dev environment we might run as user.
    # However, the emulator by default maps host user to guest root (0) in many cases or preserves it?
    # Actually `Fiberish` seems to run as current user.
    # If we are non-root on host, we are non-root in guest unless we use namespaces/fakeroot.
    # But `Fiberish` has `Hostfs` mapping.
    
    # Let's just check for start and end.
    
    case = EmulatorCase(
        name="identity_syscalls",
        binary_name="test_identity",
        expect_tokens=[
            "Starting identity tests...",
            "UIDs:",
            "GIDs:",
            "Num groups:",
            "Identity tests passed."
        ],
    )
    
    run_case(project_root, project_root / "build/integration-assets/assets", case, fiberpod_dll, alpine_image)

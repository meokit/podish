import pytest
from pathlib import Path
from .harness import EmulatorCase, run_case

@pytest.mark.integration
def test_shared_file_futex(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="shared_file_futex",
        binary_name="test_shared_file_futex",
        expect_tokens=[
            "SUCCESS",
            "[Child] Writing 1 to shared memory and waking parent...",
            "[Parent] Futex wait finished, val=1",
        ],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

@pytest.mark.integration
def test_sysv_shm_futex(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="sysv_shm_futex",
        binary_name="test_sysv_shm_futex",
        expect_tokens=[
            "SUCCESS",
            "[Child] Writing 1 to shm and waking parent...",
            "[Parent] Futex wait finished, val=1",
        ],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

@pytest.mark.integration
def test_shared_file_yield(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="shared_file_yield",
        binary_name="test_shared_file_yield",
        expect_tokens=[
            "SUCCESS",
            "[Child] Writing 1 to shared memory...",
            "[Parent] Spin wait finished",
        ],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

@pytest.mark.integration
def test_cow_private_mmap(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="cow_private_mmap",
        binary_name="test_cow_private_mmap",
        expect_tokens=[
            "SUCCESS",
            "[Child] Writing 'B' to private mapping...",
            "[Child] Read back 'B' successfully.",
            "[Parent] Value is still 'A'. COW works!",
        ],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)


@pytest.mark.integration
def test_cow_anonymous(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="cow_anonymous",
        binary_name="test_cow_anonymous",
        expect_tokens=[
            "SUCCESS",
            "Child: Writing 100 to anonymous private page",
        ],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

@pytest.mark.integration
def test_shared_futex_wake_unmapped(project_root: Path, integration_assets_dir: Path, fiberpod_dll: str, alpine_image: str) -> None:
    case = EmulatorCase(
        name="shared_futex_wake_unmapped",
        binary_name="test_shared_futex_wake_unmapped",
        expect_tokens=[
            "SUCCESS",
            "[Child] Calling futex_wake on shared memory WITHOUT touching it...",
            "[Child] Woke 1 thread(s)",
        ],
    )
    run_case(project_root, integration_assets_dir, case, fiberpod_dll, alpine_image)

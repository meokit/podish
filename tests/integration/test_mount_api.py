"""
Test cases for the new Mount API (open_tree, move_mount, mount_setattr)
and bind mount functionality.
"""
from __future__ import annotations

from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case


@pytest.mark.integration
class TestBindMount:
    """Test bind mount functionality using mount(2) with MS_BIND."""

    @pytest.mark.parametrize(
        "case",
        [
            # Bind mount a directory (if supported by the test binary)
            EmulatorCase(
                name="bind_mount_tmpfs_dir",
                binary_name="test_bind_mount",
                expect_tokens=["PASS: bind mount directory"],
                timeout=30,
            ),
        ],
    )
    def test_bind_mount_cases(
        self,
        project_root: Path,
        integration_assets_dir: Path,
        case: EmulatorCase,
        run_mode: str,
        fiberpod_dll: str | None,
        static_tests_image: str | None,
    ) -> None:
        """Run bind mount test cases."""
        if run_mode == "fiberpod":
            if case.rootfs is not None:
                case.rootfs = (project_root / case.rootfs).resolve()
            else:
                case.image = static_tests_image
        else:
            if case.rootfs is not None:
                case.rootfs = (project_root / case.rootfs).resolve()

        # Skip if test binary doesn't exist
        test_bin = integration_assets_dir / case.binary_name
        if not test_bin.exists():
            pytest.skip(f"Test binary not found: {test_bin}")

        run_case(
            project_root,
            integration_assets_dir,
            case,
            run_mode=run_mode,
            fiberpod_dll=fiberpod_dll,
        )


@pytest.mark.integration
class TestFileBindMount:
    """Test file bind mount via FiberPod -v option."""

    def test_file_bind_mount_fiberpod(
        self,
        project_root: Path,
        run_mode: str,
        fiberpod_dll: str | None,
    ) -> None:
        """Test binding a single file into the container."""
        if run_mode != "fiberpod":
            pytest.skip("File bind mount test only runs in fiberpod mode")

        import pexpect
        import tempfile

        # Use alpine rootfs which has /bin/cat
        rootfs = project_root / "tests/linux/rootfs"
        ash = rootfs / "bin/ash"
        if not ash.exists():
            pytest.skip(f"Alpine rootfs not found: {rootfs}")

        # Create a temp file with known content
        with tempfile.NamedTemporaryFile(mode='w', suffix='.txt', delete=False) as f:
            f.write("BIND_MOUNT_TEST_CONTENT\n")
            temp_file = f.name

        try:
            # Run FiberPod with the file bind-mounted
            cmd = [
                fiberpod_dll,
                "run",
                "-v", f"{temp_file}:/tmp/test_bind.txt",
                str(rootfs),
                "/bin/cat",
                "/tmp/test_bind.txt",
            ]

            child = pexpect.spawn(
                "dotnet",
                cmd,
                cwd=str(project_root),
                encoding="utf-8",
                timeout=30,
            )

            try:
                child.expect("BIND_MOUNT_TEST_CONTENT")
                child.expect(pexpect.EOF)
                child.close()
                assert child.exitstatus == 0, f"Exit status: {child.exitstatus}"
            except Exception:
                raise
            finally:
                if child.isalive():
                    child.terminate(force=True)
        finally:
            import os
            os.unlink(temp_file)


@pytest.mark.integration
class TestMountApi:
    """Test the new mount syscalls (open_tree, move_mount, mount_setattr)."""

    @pytest.mark.parametrize(
        "case",
        [
            # Test open_tree with OPEN_TREE_CLONE
            EmulatorCase(
                name="open_tree_clone",
                binary_name="test_open_tree",
                expect_tokens=["PASS: open_tree clone"],
                timeout=30,
            ),
            # Test move_mount
            EmulatorCase(
                name="move_mount_basic",
                binary_name="test_move_mount",
                expect_tokens=["PASS: move_mount basic"],
                timeout=30,
            ),
        ],
    )
    def test_mount_api_cases(
        self,
        project_root: Path,
        integration_assets_dir: Path,
        case: EmulatorCase,
        run_mode: str,
        fiberpod_dll: str | None,
        static_tests_image: str | None,
    ) -> None:
        """Run mount API test cases."""
        if run_mode == "fiberpod":
            if case.rootfs is not None:
                case.rootfs = (project_root / case.rootfs).resolve()
            else:
                case.image = static_tests_image
        else:
            if case.rootfs is not None:
                case.rootfs = (project_root / case.rootfs).resolve()

        # Skip if test binary doesn't exist
        test_bin = integration_assets_dir / case.binary_name
        if not test_bin.exists():
            pytest.skip(f"Test binary not found: {test_bin}")

        run_case(
            project_root,
            integration_assets_dir,
            case,
            run_mode=run_mode,
            fiberpod_dll=fiberpod_dll,
        )

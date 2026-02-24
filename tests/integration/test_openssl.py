"""Integration test for OpenSSL."""
from __future__ import annotations

from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case


@pytest.mark.integration
@pytest.mark.parametrize(
    "case",
    [
        EmulatorCase(
            name="openssl_version",
            binary_name="openssl",
            args=["version"],
            rootfs=Path("tests/openssl/rootfs"),
            expect_tokens=["OpenSSL 3.0"],
            timeout=30,
        ),
        EmulatorCase(
            name="openssl_dgst_md5",
            binary_name="openssl",
            args=["dgst", "-md5", "/bin/openssl"],
            rootfs=Path("tests/openssl/rootfs"),
            expect_tokens=["MD5(/bin/openssl)="],
            timeout=30,
        ),
        EmulatorCase(
            name="openssl_dgst_sha256",
            binary_name="openssl",
            args=["dgst", "-sha256", "/bin/openssl"],
            rootfs=Path("tests/openssl/rootfs"),
            expect_tokens=["SHA256(/bin/openssl)="],
            timeout=30,
        ),
        EmulatorCase(
            name="openssl_speed_md5",
            binary_name="openssl",
            args=["speed", "-seconds", "1", "md5"],
            rootfs=Path("tests/openssl/rootfs"),
            expect_tokens=["Doing md5 for 1s", "16384 size blocks"],
            timeout=60,
        ),
        EmulatorCase(
            name="openssl_speed_aes_evp",
            binary_name="openssl",
            args=["speed", "-seconds", "1", "-evp", "aes-128-cbc"],
            rootfs=Path("tests/openssl/rootfs"),
            expect_tokens=["Doing aes-128-cbc for 1s", "16384 size blocks"],
            timeout=60,
        ),
        EmulatorCase(
            name="openssl_genrsa",
            binary_name="openssl",
            args=["genrsa", "1024"],
            rootfs=Path("tests/openssl/rootfs"),
            expect_tokens=["-----BEGIN RSA PRIVATE KEY-----", "-----END RSA PRIVATE KEY-----"],
            timeout=60,
        ),
    ],
)
def test_openssl(project_root: Path, integration_assets_dir: Path, case: EmulatorCase) -> None:
    if case.rootfs is not None:
        case.rootfs = (project_root / case.rootfs).resolve()
    
    # We need to ensure the binary is actually in the rootfs
    openssl_bin = case.rootfs / "bin" / case.binary_name
    if not openssl_bin.exists():
        pytest.skip(f"OpenSSL binary not found at {openssl_bin}. Run tests/openssl/build_openssl.sh first.")

    run_case(project_root, Path(case.rootfs / "bin"), case)

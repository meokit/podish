"""Integration test for OpenSSL."""
from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case


def run_emulator_openssl(project_root: Path, rootfs: Path, args: list[str]) -> str:
    openssl_bin = rootfs / "bin" / "openssl"
    if not openssl_bin.exists():
        pytest.skip(f"OpenSSL binary not found at {openssl_bin}")
    case = EmulatorCase(name="dynamic_openssl", binary_name="openssl", args=args, rootfs=rootfs, timeout=60)
    return run_case(project_root, rootfs / "bin", case)

@pytest.mark.integration
def test_openssl_md5_correctness(project_root: Path):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    target_file = rootfs / "bin" / "openssl"
    if not target_file.exists():
        pytest.skip("OpenSSL binary missing")
    
    with open(target_file, "rb") as f:
        data = f.read()
        expected_md5 = hashlib.md5(data).hexdigest()
    
    out = run_emulator_openssl(project_root, rootfs, ["dgst", "-md5", "/bin/openssl"])
    assert expected_md5 in out.lower()

@pytest.mark.integration
def test_openssl_sha256_correctness(project_root: Path):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    target_file = rootfs / "bin" / "openssl"
    if not target_file.exists():
        pytest.skip("OpenSSL binary missing")
    
    with open(target_file, "rb") as f:
        data = f.read()
        expected_sha256 = hashlib.sha256(data).hexdigest()
    
    out = run_emulator_openssl(project_root, rootfs, ["dgst", "-sha256", "/bin/openssl"])
    assert expected_sha256 in out.lower()

@pytest.mark.integration
def test_openssl_aes_128_cbc_correctness(project_root: Path):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
    
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    test_file_host = work_dir / "plaintext.txt"
    test_file_host.write_text("Hello, this is a secret message for AES-128-CBC!\n")
    
    # Encrypt using emulator OpenSSL
    run_emulator_openssl(project_root, rootfs, [
        "enc", "-aes-128-cbc", "-in", "/tmp/plaintext.txt", "-out", "/tmp/encrypted.bin", 
        "-pass", "pass:secret", "-pbkdf2"
    ])
    
    enc_file_host = work_dir / "encrypted.bin"
    assert enc_file_host.exists()
    assert enc_file_host.stat().st_size > 0
    
    # Decrypt using host OpenSSL
    dec_file_host = work_dir / "decrypted.txt"
    subprocess.run([
        "openssl", "enc", "-d", "-aes-128-cbc", "-in", str(enc_file_host), 
        "-out", str(dec_file_host), "-pass", "pass:secret", "-pbkdf2"
    ], check=True)
    
    assert dec_file_host.read_text() == "Hello, this is a secret message for AES-128-CBC!\n"

@pytest.mark.integration
def test_openssl_rsa_keygen_correctness(project_root: Path):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
        
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    key_file_host = work_dir / "test_rsa.pem"
    if key_file_host.exists():
        key_file_host.unlink()
        
    # Generate RSA key in emulator
    run_emulator_openssl(project_root, rootfs, ["genrsa", "-out", "/tmp/test_rsa.pem", "1024"])
    
    assert key_file_host.exists()
    assert key_file_host.stat().st_size > 0
    
    # Check using host OpenSSL
    res = subprocess.run([
        "openssl", "rsa", "-in", str(key_file_host), "-check", "-noout"
    ], capture_output=True, text=True)
    
    assert res.returncode == 0
    assert "RSA key ok" in res.stdout

@pytest.mark.integration
def test_openssl_chacha20_correctness(project_root: Path):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
    
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    test_file_host = work_dir / "plaintext_chacha20.txt"
    test_file_host.write_text("Hello, this is a secret message for ChaCha20!\n")
    
    # Encrypt using emulator OpenSSL
    run_emulator_openssl(project_root, rootfs, [
        "enc", "-chacha20", "-in", "/tmp/plaintext_chacha20.txt", "-out", "/tmp/encrypted_chacha20.bin", 
        "-pass", "pass:secret", "-pbkdf2"
    ])
    
    enc_file_host = work_dir / "encrypted_chacha20.bin"
    assert enc_file_host.exists()
    assert enc_file_host.stat().st_size > 0
    
    # Decrypt using host OpenSSL
    dec_file_host = work_dir / "decrypted_chacha20.txt"
    subprocess.run([
        "openssl", "enc", "-d", "-chacha20", "-in", str(enc_file_host), 
        "-out", str(dec_file_host), "-pass", "pass:secret", "-pbkdf2"
    ], check=True)
    
    assert dec_file_host.read_text() == "Hello, this is a secret message for ChaCha20!\n"

@pytest.mark.integration
def test_openssl_ec_keygen_correctness(project_root: Path):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
        
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    key_file_host = work_dir / "test_ec.pem"
    if key_file_host.exists():
        key_file_host.unlink()
        
    # Generate EC key in emulator
    run_emulator_openssl(project_root, rootfs, ["ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", "/tmp/test_ec.pem"])
    
    assert key_file_host.exists()
    assert key_file_host.stat().st_size > 0
    
    # Check using host OpenSSL
    res = subprocess.run([
        "openssl", "ec", "-in", str(key_file_host), "-check", "-noout"
    ], capture_output=True, text=True)
    
    assert res.returncode == 0
    assert "key valid" in res.stderr.lower() or "key ok" in res.stderr.lower()

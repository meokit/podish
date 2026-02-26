"""Integration test for OpenSSL."""
from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path

import pytest

from .harness import EmulatorCase, run_case


def run_emulator_openssl(
    project_root: Path,
    rootfs: Path,
    args: list[str],
    send_eof: bool = False,
    allow_timeout: bool = False,
    timeout: int = 60,
    fiberpod_dll: str | None = None,
    alpine_image: str | None = None,
) -> str:
    openssl_bin = rootfs / "bin" / "openssl"
    if not openssl_bin.exists():
        pytest.skip(f"OpenSSL binary not found at {openssl_bin}")
    case = EmulatorCase(
        name="dynamic_openssl",
        binary_name="openssl",
        args=args,
        rootfs=rootfs,
        timeout=timeout,
        send_eof=send_eof,
        allow_timeout=allow_timeout,
    )
    return run_case(project_root, rootfs / "bin", case, fiberpod_dll, alpine_image)

@pytest.mark.integration
def test_openssl_md5_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    target_file = rootfs / "bin" / "openssl"
    if not target_file.exists():
        pytest.skip("OpenSSL binary missing")
    
    with open(target_file, "rb") as f:
        data = f.read()
        expected_md5 = hashlib.md5(data).hexdigest()
    
    out = run_emulator_openssl(project_root, rootfs, ["dgst", "-md5", "/bin/openssl"], fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)
    assert expected_md5 in out.lower()

@pytest.mark.integration
def test_openssl_sha256_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    target_file = rootfs / "bin" / "openssl"
    if not target_file.exists():
        pytest.skip("OpenSSL binary missing")
    
    with open(target_file, "rb") as f:
        data = f.read()
        expected_sha256 = hashlib.sha256(data).hexdigest()
    
    out = run_emulator_openssl(project_root, rootfs, ["dgst", "-sha256", "/bin/openssl"], fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)
    assert expected_sha256 in out.lower()

@pytest.mark.integration
def test_openssl_aes_128_cbc_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
    
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    test_file_host = work_dir / "plaintext.txt"
    test_file_host.write_text("Hello, this is a secret message for AES-128-CBC!\n")
    
    # Encrypt using emulator OpenSSL
    run_emulator_openssl(project_root, rootfs, [
        "enc", "-e", "-aes-128-cbc", "-in", "/tmp/plaintext.txt", "-out", "/tmp/encrypted.bin",
        "-pass", "pass:secret", "-pbkdf2"
    ], fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)
    
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
def test_openssl_rsa_keygen_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
        
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    key_file_host = work_dir / "test_rsa.pem"
    if key_file_host.exists():
        key_file_host.unlink()
        
    # Generate RSA key in emulator
    run_emulator_openssl(project_root, rootfs, ["genrsa", "-out", "/tmp/test_rsa.pem", "1024"], fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)
    
    assert key_file_host.exists()
    assert key_file_host.stat().st_size > 0
    
    # Check using host OpenSSL
    res = subprocess.run([
        "openssl", "rsa", "-in", str(key_file_host), "-check", "-noout"
    ], capture_output=True, text=True)
    
    assert res.returncode == 0
    assert "RSA key ok" in res.stdout

@pytest.mark.integration
def test_openssl_chacha20_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
    
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    test_file_host = work_dir / "plaintext_chacha20.txt"
    test_file_host.write_text("Hello, this is a secret message for ChaCha20!\n")
    
    # Encrypt using emulator OpenSSL
    run_emulator_openssl(project_root, rootfs, [
        "enc", "-e", "-chacha20", "-in", "/tmp/plaintext_chacha20.txt", "-out", "/tmp/encrypted_chacha20.bin",
        "-pass", "pass:secret", "-pbkdf2"
    ], fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)
    
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
def test_openssl_ec_keygen_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")
        
    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)
    
    key_file_host = work_dir / "test_ec.pem"
    if key_file_host.exists():
        key_file_host.unlink()
        
    # Generate EC key in emulator
    run_emulator_openssl(project_root, rootfs, ["ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", "/tmp/test_ec.pem"], fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)
    
    assert key_file_host.exists()
    assert key_file_host.stat().st_size > 0
    
    # Check using host OpenSSL
    res = subprocess.run([
        "openssl", "ec", "-in", str(key_file_host), "-check", "-noout"
    ], capture_output=True, text=True)
    
    assert res.returncode == 0
    assert "key valid" in res.stderr.lower() or "key ok" in res.stderr.lower()

@pytest.mark.integration
def test_openssl_s_client_correctness(project_root: Path, fiberpod_dll: str, alpine_image: str):
    rootfs = (project_root / "tests" / "openssl" / "rootfs").resolve()
    if not (rootfs / "bin" / "openssl").exists():
        pytest.skip("OpenSSL binary missing")

    work_dir = rootfs / "tmp"
    work_dir.mkdir(exist_ok=True)

    cert_path = work_dir / "server.crt"
    key_path = work_dir / "server.key"

    # Reuse cert/key when available to keep test fast.
    if not cert_path.exists() or not key_path.exists():
        run_emulator_openssl(project_root, rootfs, [
            "req", "-x509", "-newkey", "rsa:2048",
            "-keyout", "/tmp/server.key", "-out", "/tmp/server.crt",
            "-days", "1", "-nodes", "-subj", "/CN=localhost"
        ], timeout=20, fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)

    assert cert_path.exists()
    assert key_path.exists()

    import ssl
    import socket
    import threading
    import traceback

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certfile=str(cert_path), keyfile=str(key_path))

    bindsocket = socket.socket()
    bindsocket.bind(('127.0.0.1', 0))
    bindsocket.listen(1)
    port = bindsocket.getsockname()[1]

    success = False
    close_notify_ok = False

    def serve():
        nonlocal success, close_notify_ok
        try:
            newsocket, peer = bindsocket.accept()
            print(f"[host-server] accepted peer={peer}", flush=True)
            newsocket.settimeout(5)
            try:
                # Peek raw TCP payload without consuming, useful to verify ClientHello reaches host.
                peek = newsocket.recv(64, socket.MSG_PEEK)
                print(f"[host-server] tcp-peek {len(peek)} bytes: {peek[:32].hex()}", flush=True)
            except Exception as peek_err:
                print(f"[host-server] tcp-peek error: {peek_err}", flush=True)

            print("[host-server] entering TLS wrap_socket()", flush=True)
            connstream = context.wrap_socket(newsocket, server_side=True)
            print(f"[host-server] tls-handshake-ok cipher={connstream.cipher()}", flush=True)
            connstream.sendall(b"Hello from host TLS server!\n")
            print("[host-server] sent app data", flush=True)
            success = True
            connstream.unwrap()
            close_notify_ok = True
            print("[host-server] unwrap done", flush=True)
            connstream.close()
        except Exception as e:
            print("Server error:", e)
            print(traceback.format_exc(), flush=True)
        finally:
            bindsocket.close()

    t = threading.Thread(target=serve, daemon=True)
    t.start()

    # Run guest openssl s_client
    out = run_emulator_openssl(project_root, rootfs, [
        "s_client", "-connect", f"127.0.0.1:{port}", "-brief", "-no_ign_eof"
    ], send_eof=True, timeout=15, fiberpod_dll=fiberpod_dll, alpine_image=alpine_image)

    t.join(timeout=5)

    assert success
    assert close_notify_ok
    assert "Hello from host TLS server!" in out
    assert "CONNECTION ESTABLISHED" in out

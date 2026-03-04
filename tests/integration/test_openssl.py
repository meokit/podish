"""Integration tests for OpenSSL running in Alpine OCI images."""
from __future__ import annotations

import hashlib
import socket
import ssl
import subprocess
import shutil
import threading
import traceback
from pathlib import Path

import pytest

from .harness import run_fiberpod_command

@pytest.fixture(scope="session")
def openssl_rootfs(
    project_root: Path,
    fiberpod_dll: str,
    tmp_path_factory: pytest.TempPathFactory,
) -> str:
    source_rootfs = project_root / ".fiberpod" / "images" / "docker.io_i386_alpine_latest"
    if not source_rootfs.exists():
        subprocess.run(
            [
                "dotnet",
                "run",
                "--project",
                str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
                "--no-build",
                "--",
                "pull",
                "docker.io/i386/alpine:latest",
            ],
            cwd=project_root,
            check=True,
        )

    rootfs = tmp_path_factory.mktemp("openssl-rootfs") / "rootfs"
    shutil.copytree(source_rootfs, rootfs, symlinks=True)

    run_fiberpod_command(
        project_root=project_root,
        fiberpod_dll=fiberpod_dll,
        image_or_rootfs=str(rootfs),
        use_rootfs=True,
        command="/sbin/apk",
        args=["add", "--no-cache", "openssl"],
        timeout=120,
    )
    return str(rootfs)


def run_emulator_openssl(
    project_root: Path,
    fiberpod_dll: str,
    rootfs: str,
    args: list[str],
    work_dir: Path | None = None,
    send_eof: bool = False,
    allow_timeout: bool = False,
    timeout: int = 60,
) -> str:
    volumes = [(str(work_dir), "/work")] if work_dir else None
    return run_fiberpod_command(
        project_root=project_root,
        fiberpod_dll=fiberpod_dll,
        image_or_rootfs=rootfs,
        use_rootfs=True,
        command="/usr/bin/openssl",
        args=args,
        volumes=volumes,
        timeout=timeout,
        send_eof=send_eof,
        allow_timeout=allow_timeout,
    )


@pytest.mark.integration
def test_openssl_md5_correctness(project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path):
    data = b"fiberish-openssl-md5\n"
    (tmp_path / "input.txt").write_bytes(data)
    expected_md5 = hashlib.md5(data).hexdigest()

    out = run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        ["dgst", "-md5", "/work/input.txt"],
        work_dir=tmp_path,
    )
    assert expected_md5 in out.lower()


@pytest.mark.integration
def test_openssl_sha256_correctness(project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path):
    data = b"fiberish-openssl-sha256\n"
    (tmp_path / "input.txt").write_bytes(data)
    expected_sha256 = hashlib.sha256(data).hexdigest()

    out = run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        ["dgst", "-sha256", "/work/input.txt"],
        work_dir=tmp_path,
    )
    assert expected_sha256 in out.lower()


@pytest.mark.integration
def test_openssl_aes_128_cbc_correctness(
    project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path
):
    plaintext = "Hello, this is a secret message for AES-128-CBC!\n"
    (tmp_path / "plaintext.txt").write_text(plaintext)

    run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        [
            "enc",
            "-e",
            "-aes-128-cbc",
            "-in",
            "/work/plaintext.txt",
            "-out",
            "/work/encrypted.bin",
            "-pass",
            "pass:secret",
            "-pbkdf2",
        ],
        work_dir=tmp_path,
    )

    enc_file_host = tmp_path / "encrypted.bin"
    assert enc_file_host.exists()
    assert enc_file_host.stat().st_size > 0

    dec_file_host = tmp_path / "decrypted.txt"
    subprocess.run(
        [
            "openssl",
            "enc",
            "-d",
            "-aes-128-cbc",
            "-in",
            str(enc_file_host),
            "-out",
            str(dec_file_host),
            "-pass",
            "pass:secret",
            "-pbkdf2",
        ],
        check=True,
    )

    assert dec_file_host.read_text() == plaintext


@pytest.mark.integration
def test_openssl_rsa_keygen_correctness(
    project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path
):
    key_file_host = tmp_path / "test_rsa.pem"

    run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        ["genrsa", "-out", "/work/test_rsa.pem", "1024"],
        work_dir=tmp_path,
    )

    assert key_file_host.exists()
    assert key_file_host.stat().st_size > 0

    res = subprocess.run(
        ["openssl", "rsa", "-in", str(key_file_host), "-check", "-noout"],
        capture_output=True,
        text=True,
    )
    assert res.returncode == 0
    assert "RSA key ok" in res.stdout


@pytest.mark.integration
def test_openssl_chacha20_correctness(
    project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path
):
    plaintext = "Hello, this is a secret message for ChaCha20!\n"
    (tmp_path / "plaintext_chacha20.txt").write_text(plaintext)

    run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        [
            "enc",
            "-e",
            "-chacha20",
            "-in",
            "/work/plaintext_chacha20.txt",
            "-out",
            "/work/encrypted_chacha20.bin",
            "-pass",
            "pass:secret",
            "-pbkdf2",
        ],
        work_dir=tmp_path,
    )

    enc_file_host = tmp_path / "encrypted_chacha20.bin"
    assert enc_file_host.exists()
    assert enc_file_host.stat().st_size > 0

    dec_file_host = tmp_path / "decrypted_chacha20.txt"
    subprocess.run(
        [
            "openssl",
            "enc",
            "-d",
            "-chacha20",
            "-in",
            str(enc_file_host),
            "-out",
            str(dec_file_host),
            "-pass",
            "pass:secret",
            "-pbkdf2",
        ],
        check=True,
    )
    assert dec_file_host.read_text() == plaintext


@pytest.mark.integration
def test_openssl_ec_keygen_correctness(
    project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path
):
    key_file_host = tmp_path / "test_ec.pem"

    run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        ["ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", "/work/test_ec.pem"],
        work_dir=tmp_path,
    )

    assert key_file_host.exists()
    assert key_file_host.stat().st_size > 0

    res = subprocess.run(
        ["openssl", "ec", "-in", str(key_file_host), "-check", "-noout"],
        capture_output=True,
        text=True,
    )
    assert res.returncode == 0
    assert "key valid" in res.stderr.lower() or "key ok" in res.stderr.lower()


@pytest.mark.integration
def test_openssl_s_client_correctness(
    project_root: Path, fiberpod_dll: str, openssl_rootfs: str, tmp_path: Path
):
    cert_path = tmp_path / "server.crt"
    key_path = tmp_path / "server.key"

    if not cert_path.exists() or not key_path.exists():
        run_emulator_openssl(
            project_root,
            fiberpod_dll,
            openssl_rootfs,
            [
                "req",
                "-x509",
                "-newkey",
                "rsa:2048",
                "-keyout",
                "/work/server.key",
                "-out",
                "/work/server.crt",
                "-days",
                "1",
                "-nodes",
                "-subj",
                "/CN=localhost",
            ],
            timeout=30,
            work_dir=tmp_path,
        )

    assert cert_path.exists()
    assert key_path.exists()

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certfile=str(cert_path), keyfile=str(key_path))

    bindsocket = socket.socket()
    bindsocket.bind(("127.0.0.1", 0))
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
        except Exception as exc:
            print("Server error:", exc)
            print(traceback.format_exc(), flush=True)
        finally:
            bindsocket.close()

    t = threading.Thread(target=serve, daemon=True)
    t.start()

    out = run_emulator_openssl(
        project_root,
        fiberpod_dll,
        openssl_rootfs,
        ["s_client", "-connect", f"127.0.0.1:{port}", "-brief", "-no_ign_eof"],
        send_eof=True,
        timeout=20,
    )

    t.join(timeout=5)

    assert success
    assert close_notify_ok
    assert "Hello from host TLS server!" in out
    assert "CONNECTION ESTABLISHED" in out

"""End-to-end integration tests for CLI port forwarding."""
from __future__ import annotations

import os
import socket
from pathlib import Path

import pexpect
import pytest


def _spawn_forward_server(
    project_root: Path,
    integration_assets_dir: Path,
    alpine_image: str,
    container_ports: list[int],
    host_ports: list[int],
) -> pexpect.spawn:
    assert len(container_ports) == len(host_ports)

    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
    ]

    for host_port, container_port in zip(host_ports, container_ports):
        cmd.extend(["-p", f"{host_port}:{container_port}"])

    cmd.extend([
        "-v", f"{integration_assets_dir}:/tests",
        alpine_image,
        "--",
        "/tests/test_cli_port_forward_server",
        *[str(port) for port in container_ports],
    ])

    clean_env = {
        "TERM": os.environ.get("TERM", "xterm"),
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "DOTNET_CLI_HOME": os.path.expanduser("~"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_NOLOGO": "true",
    }

    return pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
        env=clean_env,
    )


def _connect_once(host_port: int, payload: bytes | None = None) -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(5)
        s.connect(("127.0.0.1", host_port))
        if payload is not None:
            s.sendall(payload)


def _roundtrip(host_port: int, payload: bytes) -> bytes:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(5)
        s.connect(("127.0.0.1", host_port))
        s.sendall(payload)
        return s.recv(1024)


def test_run_private_network_with_published_port(
    project_root: Path,
    integration_assets_dir: Path,
    alpine_image: str,
) -> None:
    host_port = 54321
    container_port = 12345

    child = _spawn_forward_server(project_root, integration_assets_dir, alpine_image, [container_port], [host_port])
    output = ""
    try:
        child.expect_exact("READY", timeout=20)
        output += child.before or ""
        output += "READY"

        response = _roundtrip(host_port, b"TEST\n")
        assert response == b"ACK:12345:TEST\n"

        child.expect_exact("HANDLED:12345", timeout=10)
        output += child.before or ""
        output += "HANDLED:12345"
        child.expect_exact("DONE", timeout=10)
        output += child.before or ""
        output += "DONE"
        child.expect(pexpect.EOF, timeout=10)
    except Exception as exc:
        output += child.before or ""
        raise AssertionError(f"Published port integration failed. Output:\n{output}") from exc
    finally:
        if child.isalive():
            child.terminate(force=True)
        child.close()


def test_run_private_network_dynamic_host_port(
    project_root: Path,
    alpine_image: str,
) -> None:
    container_port = 12346
    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", str(container_port),
        alpine_image,
        "echo",
        "test",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    try:
        child.expect(pexpect.EOF, timeout=10)
    except pexpect.exceptions.TIMEOUT:
        child.close(force=True)

    output = child.before or ""
    child.close()

    assert child.exitstatus == 125, (
        f"Expected exit code 125 for invalid port format, got {child.exitstatus}. Output:\n{output}"
    )
    assert "invalid" in output.lower(), (
        f"Expected error about invalid port format. Output:\n{output}"
    )


def test_run_private_network_multiple_published_ports(
    project_root: Path,
    integration_assets_dir: Path,
    alpine_image: str,
) -> None:
    host_ports = [54322, 54323]
    container_ports = [12347, 12348]

    child = _spawn_forward_server(project_root, integration_assets_dir, alpine_image, container_ports, host_ports)
    output = ""
    try:
        child.expect_exact("READY", timeout=20)
        output += child.before or ""
        output += "READY"

        response1 = _roundtrip(host_ports[0], b"ONE\n")
        response2 = _roundtrip(host_ports[1], b"TWO\n")
        assert response1 == b"ACK:12347:ONE\n"
        assert response2 == b"ACK:12348:TWO\n"

        child.expect_exact("HANDLED:12347", timeout=10)
        output += child.before or ""
        output += "HANDLED:12347"
        child.expect_exact("HANDLED:12348", timeout=10)
        output += child.before or ""
        output += "HANDLED:12348"
        child.expect_exact("DONE", timeout=10)
        output += child.before or ""
        output += "DONE"
        child.expect(pexpect.EOF, timeout=10)
    except Exception as exc:
        output += child.before or ""
        raise AssertionError(f"Multiple published ports integration failed. Output:\n{output}") from exc
    finally:
        if child.isalive():
            child.terminate(force=True)
        child.close()


def test_run_private_network_published_port_closed_target_disconnects(
    project_root: Path,
    alpine_image: str,
) -> None:
    host_port = 54324
    container_port = 12349

    cmd = [
        "run",
        "--project", str(project_root / "Podish.Cli/Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network", "private",
        "-p", f"{host_port}:{container_port}",
        alpine_image,
        "/bin/sh", "-c", "echo READY && sleep 6",
    ]

    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=30,
    )

    output = ""
    try:
        child.expect_exact("READY", timeout=15)
        output += child.before or ""
        output += "READY"

        disconnected = False
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.settimeout(2)
            s.connect(("127.0.0.1", host_port))
            try:
                s.sendall(b"CLOSED\n")
            except (BrokenPipeError, ConnectionResetError, OSError):
                disconnected = True

            if not disconnected:
                try:
                    data = s.recv(128)
                    disconnected = (data == b"")
                except (ConnectionResetError, OSError):
                    disconnected = True

        assert disconnected, "Expected published port to close/reset when target container port is not listening"
    except Exception as exc:
        output += child.before or ""
        raise AssertionError(f"Closed-target published port integration failed. Output:\n{output}") from exc
    finally:
        if child.isalive():
            child.terminate(force=True)
        child.close()

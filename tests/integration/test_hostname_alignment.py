from pathlib import Path
import subprocess
import uuid


def _run_hostname_probe(project_root: Path, *extra_args: str) -> subprocess.CompletedProcess[str]:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        *extra_args,
        str(project_root / ".fiberpod" / "oci" / "images" / "docker.io_i386_alpine_latest"),
        "--",
        "/bin/sh",
        "-lc",
        "cat /etc/hostname && uname -n && cat /proc/sys/kernel/hostname && printf '%s\\n' '---HOSTS---' && cat /etc/hosts",
    ]

    return subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=20,
        check=False,
    )


def test_container_name_sets_default_hostname(project_root: Path) -> None:
    name = f"podish-hostname-default-{uuid.uuid4().hex[:8]}"
    proc = _run_hostname_probe(project_root, "--name", name)

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output

    lines = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
    assert lines[:3] == [
        name,
        name,
        name,
    ], output
    assert lines[3:] == [
        "---HOSTS---",
        "127.0.0.1 localhost",
        f"127.0.1.1 {name}",
        "::1 localhost ip6-localhost ip6-loopback",
    ], output


def test_explicit_hostname_overrides_container_name(project_root: Path) -> None:
    name = f"podish-hostname-name-{uuid.uuid4().hex[:8]}"
    hostname = f"podish-hostname-guest-{uuid.uuid4().hex[:8]}"
    proc = _run_hostname_probe(
        project_root,
        "--name",
        name,
        "--hostname",
        hostname,
    )

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output

    lines = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
    assert lines[:3] == [
        hostname,
        hostname,
        hostname,
    ], output
    assert lines[3:] == [
        "---HOSTS---",
        "127.0.0.1 localhost",
        f"127.0.1.1 {hostname} {name}",
        "::1 localhost ip6-localhost ip6-loopback",
    ], output

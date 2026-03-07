from pathlib import Path
import subprocess


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
        "cat /etc/hostname && uname -n && cat /proc/sys/kernel/hostname",
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
    proc = _run_hostname_probe(project_root, "--name", "podish-hostname-default")

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output

    lines = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
    assert lines == [
        "podish-hostname-default",
        "podish-hostname-default",
        "podish-hostname-default",
    ], output


def test_explicit_hostname_overrides_container_name(project_root: Path) -> None:
    proc = _run_hostname_probe(
        project_root,
        "--name",
        "podish-hostname-name",
        "--hostname",
        "podish-hostname-guest",
    )

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output

    lines = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
    assert lines == [
        "podish-hostname-guest",
        "podish-hostname-guest",
        "podish-hostname-guest",
    ], output

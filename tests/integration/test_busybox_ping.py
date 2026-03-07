from pathlib import Path
import subprocess


def test_busybox_ping_localhost(project_root: Path) -> None:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "docker.io/i386/alpine:latest",
        "--",
        "/bin/ping",
        "-c",
        "1",
        "127.0.0.1",
    ]

    proc = subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output
    assert "PING 127.0.0.1" in output, output
    assert "1 packets transmitted, 1 packets received" in output, output

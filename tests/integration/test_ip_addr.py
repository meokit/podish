import subprocess
from pathlib import Path


def test_ip_addr_private_network_outputs_lo_and_eth0(project_root: Path, alpine_image: str) -> None:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--network",
        "private",
        alpine_image,
        "--",
        "/bin/sh",
        "-lc",
        "ip addr",
    ]

    proc = subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=20,
        check=False,
    )

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output
    assert "1: lo:" in output, output
    assert "2: eth0:" in output, output
    assert "inet 127.0.0.1/8" in output, output
    assert "inet 10.88.0.2/24" in output, output
    assert "ioctl 0x8942 failed" not in output, output

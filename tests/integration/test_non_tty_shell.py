import subprocess
from pathlib import Path


def test_non_tty_shell_does_not_emit_terminal_queries(project_root: Path, alpine_image: str) -> None:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        alpine_image,
        "--",
        "/bin/sh",
    ]

    proc = subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=15,
        check=False,
    )

    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output
    assert "^[[39;5R" not in output, output
    assert "\x1b[39;5R" not in output, output
    assert "/ #" not in output, output

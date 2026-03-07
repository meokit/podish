from pathlib import Path
import subprocess


def test_warn_level_does_not_create_engine_session_log(project_root: Path, alpine_image: str) -> None:
    logs_dir = project_root / ".fiberpod" / "logs"
    logs_dir.mkdir(parents=True, exist_ok=True)
    before = {p.name for p in logs_dir.glob("engine_*.log")}

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
        "/bin/true",
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

    after = {p.name for p in logs_dir.glob("engine_*.log")}
    assert after == before, output

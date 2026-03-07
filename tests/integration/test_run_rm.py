import subprocess
import uuid
from pathlib import Path


def test_run_rm_removes_container_metadata(project_root: Path, alpine_image: str) -> None:
    containers_dir = project_root / ".fiberpod" / "containers"
    containers_dir.mkdir(parents=True, exist_ok=True)
    before = {p.name for p in containers_dir.iterdir() if p.is_dir()}
    name = f"podish-rm-{uuid.uuid4().hex[:8]}"

    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--name",
        name,
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

    after = {p.name for p in containers_dir.iterdir() if p.is_dir()}
    assert after == before, output

from __future__ import annotations

import subprocess
from pathlib import Path


def _run_with_init(project_root: Path, alpine_image: str, script: str, timeout: int = 25) -> subprocess.CompletedProcess[str]:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--init",
        alpine_image,
        "--",
        "/bin/sh",
        "-c",
        script,
    ]
    return subprocess.run(
        cmd,
        cwd=str(project_root),
        capture_output=True,
        text=True,
        timeout=timeout,
        check=False,
    )


def test_init_mode_forwards_signal_to_child(project_root: Path, alpine_image: str) -> None:
    script = "trap 'echo FORWARDED_TERM; exit 42' TERM; echo READY; kill -TERM 1; sleep 3; echo NOT_REACHED"
    proc = _run_with_init(project_root, alpine_image, script)
    output = proc.stdout + proc.stderr
    assert proc.returncode == 42, output
    assert "FORWARDED_TERM" in output, output
    assert "NOT_REACHED" not in output, output


def test_init_mode_reaps_orphan_zombie(project_root: Path, alpine_image: str) -> None:
    script = (
        "pid=$(/bin/sh -c '(sleep 1) & echo $!; exit 0'); "
        "echo ORPHAN_PID:$pid; "
        "sleep 2; "
        "if [ -e /proc/$pid/stat ]; then "
        "  st=$(awk '{print $3}' /proc/$pid/stat); "
        "  echo ORPHAN_STATE:$st; "
        "else "
        "  echo ORPHAN_REAPED; "
        "fi"
    )
    proc = _run_with_init(project_root, alpine_image, script)
    output = proc.stdout + proc.stderr
    assert proc.returncode == 0, output
    assert "ORPHAN_PID:" in output, output
    assert "ORPHAN_REAPED" in output, output
    assert "ORPHAN_STATE:Z" not in output, output


def test_init_mode_forwards_sigkill_to_child(project_root: Path, alpine_image: str) -> None:
    script = "echo BEFORE_KILL; kill -KILL 1; sleep 5; echo NOT_REACHED"
    proc = _run_with_init(project_root, alpine_image, script)
    output = proc.stdout + proc.stderr
    assert proc.returncode == 137, output
    assert "BEFORE_KILL" in output, output
    assert "NOT_REACHED" not in output, output

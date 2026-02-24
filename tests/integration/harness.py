from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

import pexpect


@dataclass
class EmulatorCase:
    name: str
    binary_name: str
    args: list[str] = field(default_factory=list)
    rootfs: Path | None = None
    expect_tokens: list[str] = field(default_factory=list)
    timeout: int = 30
    send_eof: bool = False
    allow_timeout: bool = False


def _dotnet_cmd(
    project_root: Path, test_bin: Path, rootfs: Path | None, args: Iterable[str]
) -> tuple[str, list[str]]:
    cli_project = project_root / "Fiberish.Cli" / "Fiberish.Cli.csproj"
    cmd = [
        "run",
        "--project",
        str(cli_project),
        "--no-build",
        "--",
    ]
    if rootfs:
        cmd.extend(["--rootfs", str(rootfs)])
    cmd.append("--")
    cmd.append(str(test_bin))
    cmd.extend(args)
    return "dotnet", cmd


def run_case(project_root: Path, assets_dir: Path, case: EmulatorCase) -> str:
    test_bin = assets_dir / case.binary_name
    if not test_bin.exists():
        raise AssertionError(f"Missing integration binary: {test_bin}")

    dotnet, args = _dotnet_cmd(project_root, test_bin, case.rootfs, case.args)
    child = pexpect.spawn(
        dotnet,
        args,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=case.timeout,
    )
    if case.send_eof:
        child.sendeof()

    timed_out = False
    try:
        child.expect(pexpect.EOF)
    except pexpect.exceptions.TIMEOUT:
        if not case.allow_timeout:
            raise
        timed_out = True
    output = child.before or ""
    if timed_out:
        child.close(force=True)
    else:
        child.close()

    if not timed_out and child.exitstatus != 0:
        raise AssertionError(
            f"[{case.name}] emulator exited with {child.exitstatus}\nOutput:\n{output}"
        )

    for token in case.expect_tokens:
        if token not in output:
            raise AssertionError(
                f"[{case.name}] missing token {token!r}\nOutput:\n{output}"
            )

    return output

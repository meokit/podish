#!/usr/bin/env python3

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def default_rootfs() -> Path:
    return Path(__file__).resolve().parent / "rootfs" / "coremark_i386_alpine"


def run(cmd: list[str], cwd: Path) -> None:
    print(f"[superopcode] {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=str(cwd), check=False)
    if result.returncode != 0:
        raise RuntimeError(f"command failed with exit code {result.returncode}: {' '.join(cmd)}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "One-shot SuperOpcode pipeline: build without superopcodes, profile workloads, "
            "score global 2-gram candidates from hot anchors and dependency weights, emit "
            "generated source, then optionally rebuild with superopcodes enabled."
        )
    )
    parser.add_argument("--project-root", default=str(repo_root()), help="Repository root")
    parser.add_argument("--rootfs", default=str(default_rootfs()), help="Prepared rootfs directory")
    parser.add_argument(
        "--case",
        action="append",
        choices=("compress", "compile", "run"),
        dest="cases",
        help="Benchmark case to run. Repeat for multiple cases; default runs all.",
    )
    parser.add_argument("--repeat", type=int, default=1, help="Samples per case")
    parser.add_argument("--iterations", type=int, default=3000, help="CoreMark iterations")
    parser.add_argument("--timeout", type=int, default=1800, help="Runner timeout in seconds")
    parser.add_argument("--results-dir", default=None, help="Optional runner results directory")
    parser.add_argument("--work-dir", default=None, help="Optional runner work directory")
    parser.add_argument("--reuse-rootfs", action="store_true", help="Run directly on the prepared rootfs")
    parser.add_argument("--keep-workdirs", action="store_true", help="Keep copied rootfs directories")
    parser.add_argument("--candidate-top", type=int, default=100, help="Aggregate candidate count to keep")
    parser.add_argument("--superopcode-top", type=int, default=32, help="Generated SuperOpcode count to emit")
    parser.add_argument(
        "--generated-output",
        default="libfibercpu/generated/superopcodes.generated.cpp",
        help="Generated SuperOpcode source path",
    )
    parser.add_argument(
        "--skip-verify-build",
        action="store_true",
        help="Skip the final rebuild with EnableSuperOpcodes=true after generation",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    project_root = Path(args.project_root).resolve()
    rootfs = Path(args.rootfs).resolve()
    if args.results_dir:
        results_dir = Path(args.results_dir).resolve()
    else:
        timestamp = f"{time.strftime('%Y%m%d-%H%M%S')}-superopcode"
        results_dir = project_root / "benchmark" / "podish_perf" / "results" / timestamp
    work_dir = Path(args.work_dir).resolve() if args.work_dir else None
    generated_output = (project_root / args.generated_output).resolve()

    runner_cmd = [
        sys.executable,
        str(project_root / "benchmark" / "podish_perf" / "runner.py"),
        "--engine",
        "jit",
        "--jit-handler-profile-block-dump",
        "--disable-superopcodes",
        "--block-n-gram",
        "2",
        "--aggregate-superopcode-candidates",
        "--candidate-top",
        str(args.candidate_top),
        "--rootfs",
        str(rootfs),
        "--repeat",
        str(args.repeat),
        "--iterations",
        str(args.iterations),
        "--timeout",
        str(args.timeout),
    ]

    for case in args.cases or ("compress", "compile", "run"):
        runner_cmd.extend(["--case", case])
    runner_cmd.extend(["--results-dir", str(results_dir)])
    if work_dir is not None:
        runner_cmd.extend(["--work-dir", str(work_dir)])
    if args.reuse_rootfs:
        runner_cmd.append("--reuse-rootfs")
    if args.keep_workdirs:
        runner_cmd.append("--keep-workdirs")

    run(runner_cmd, cwd=project_root)

    candidate_json = results_dir / "superopcode_candidates.json"
    generate_cmd = [
        sys.executable,
        str(project_root / "scripts" / "gen_superopcodes.py"),
        "--input",
        str(candidate_json),
        "--output",
        str(generated_output),
        "--top",
        str(args.superopcode_top),
    ]
    run(generate_cmd, cwd=project_root)

    if not args.skip_verify_build:
        verify_cmd = [
            "dotnet",
            "build",
            str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
            "-c",
            "Release",
            "-p:EnableHandlerProfile=true",
            "-p:EnableSuperOpcodes=true",
        ]
        run(verify_cmd, cwd=project_root)

    print(f"[superopcode] results_dir={results_dir}")
    print(f"[superopcode] candidate_json={candidate_json}")
    print(f"[superopcode] generated_output={generated_output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import statistics
import sys
import tempfile
import time
from dataclasses import asdict, dataclass
from pathlib import Path

import pexpect

MARKER_BEGIN = "__PODISH_BENCH_BEGIN__"
MARKER_END = "__PODISH_BENCH_END__"
DEFAULT_CASES = ("compress", "compile", "run")


@dataclass
class SampleResult:
    case: str
    iteration: int
    seconds: float
    transcript: str
    work_rootfs: str
    coremark_score: float | None = None


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def default_rootfs() -> Path:
    return Path(__file__).resolve().parent / "rootfs" / "coremark_i386_alpine"


def build_guest_script(case: str, iterations: int) -> str:
    compile_cmd = (
        f'make PORT_DIR=linux ITERATIONS={iterations} '
        'XCFLAGS="-O3 -DPERFORMANCE_RUN=1" REBUILD=1 compile'
    )

    if case == "compress":
        return f"""
set -eu
rm -rf /tmp/coremark.tar /tmp/coremark.tar.gz /tmp/coremark-restored.tar /tmp/coremark-unpack
mkdir -p /tmp/coremark-unpack
sync >/dev/null 2>&1 || true
echo {MARKER_BEGIN}
tar -C / -cf /tmp/coremark.tar coremark
gzip -1 -c /tmp/coremark.tar > /tmp/coremark.tar.gz
gzip -dc /tmp/coremark.tar.gz > /tmp/coremark-restored.tar
tar -C /tmp/coremark-unpack -xf /tmp/coremark-restored.tar
test -f /tmp/coremark-unpack/coremark/Makefile
echo {MARKER_END}
"""
    if case == "compile":
        return f"""
set -eu
cd /coremark
make clean >/dev/null 2>&1 || true
sync >/dev/null 2>&1 || true
echo {MARKER_BEGIN}
{compile_cmd}
test -x /coremark/coremark.exe
echo {MARKER_END}
"""
    if case == "run":
        return f"""
set -eu
cd /coremark
test -x ./coremark.exe || {compile_cmd} >/dev/null
sync >/dev/null 2>&1 || true
echo {MARKER_BEGIN}
./coremark.exe 0x0 0x0 0x66 {iterations}
echo {MARKER_END}
"""
    raise ValueError(f"unknown case: {case}")


def build_podish_args(project_root: Path, rootfs: Path, script: str) -> list[str]:
    return [
        "run",
        "--project",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "--no-build",
        "--",
        "run",
        "--rm",
        "--rootfs",
        str(rootfs),
        "--",
        "/bin/sh",
        "-lc",
        script,
    ]


def clean_env() -> dict[str, str]:
    return {
        "TERM": os.environ.get("TERM", "xterm"),
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "DOTNET_CLI_HOME": os.path.expanduser("~"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_NOLOGO": "true",
    }


def create_work_rootfs(base_rootfs: Path, case: str, iteration: int, work_dir: Path, reuse_rootfs: bool) -> Path:
    if reuse_rootfs:
        return base_rootfs

    work_rootfs = Path(
        tempfile.mkdtemp(prefix=f"{case}-{iteration}-", dir=str(work_dir))
    )
    shutil.rmtree(work_rootfs)
    shutil.copytree(base_rootfs, work_rootfs, symlinks=True)
    return work_rootfs


def extract_coremark_score(output: str) -> float | None:
    match = re.search(r"Iterations/Sec\s*:\s*([0-9.]+)", output)
    if match:
        return float(match.group(1))
    match = re.search(r"CoreMark 1\.0\s*:\s*([0-9.]+)", output)
    if match:
        return float(match.group(1))
    return None


def run_sample(
    project_root: Path,
    base_rootfs: Path,
    case: str,
    iteration: int,
    timeout: int,
    iterations: int,
    work_dir: Path,
    results_dir: Path,
    reuse_rootfs: bool,
    keep_workdirs: bool,
) -> SampleResult:
    work_rootfs = create_work_rootfs(base_rootfs, case, iteration, work_dir, reuse_rootfs)
    transcript = results_dir / f"{case}-{iteration:02d}.log"
    script = build_guest_script(case, iterations)
    args = build_podish_args(project_root, work_rootfs, script)
    start = 0.0
    end = 0.0

    child = pexpect.spawn(
        "dotnet",
        args,
        cwd=str(project_root),
        encoding="utf-8",
        timeout=timeout,
        env=clean_env(),
    )

    timed_output = ""
    with transcript.open("w", encoding="utf-8") as logf:
        child.logfile_read = logf
        try:
            child.expect(MARKER_BEGIN)
            start = time.perf_counter()
            child.expect(MARKER_END)
            end = time.perf_counter()
            timed_output = child.before or ""
            child.expect(pexpect.EOF)
        except pexpect.ExceptionPexpect:
            child.close(force=True)
            raise

    child.close()
    if child.exitstatus != 0 or child.signalstatus is not None:
        raise RuntimeError(
            f"{case} iteration {iteration} failed with exit={child.exitstatus} "
            f"signal={child.signalstatus}; see {transcript}"
        )
    if not keep_workdirs and not reuse_rootfs:
        shutil.rmtree(work_rootfs, ignore_errors=True)

    return SampleResult(
        case=case,
        iteration=iteration,
        seconds=end - start,
        transcript=str(transcript),
        work_rootfs=str(work_rootfs),
        coremark_score=extract_coremark_score(timed_output) if case == "run" else None,
    )


def print_summary(results: list[SampleResult]) -> None:
    grouped: dict[str, list[SampleResult]] = {}
    for sample in results:
        grouped.setdefault(sample.case, []).append(sample)

    print("")
    print("Case      Samples  Min(s)  Median(s)  Mean(s)  Notes")
    print("--------  -------  ------  ---------  -------  -----")
    for case in DEFAULT_CASES:
        samples = grouped.get(case, [])
        if not samples:
            continue
        durations = [sample.seconds for sample in samples]
        notes = ""
        scores = [sample.coremark_score for sample in samples if sample.coremark_score is not None]
        if scores:
            notes = f"Iterations/Sec median={statistics.median(scores):.2f}"
        print(
            f"{case:<8}  {len(samples):>7}  {min(durations):>6.3f}  "
            f"{statistics.median(durations):>9.3f}  {statistics.mean(durations):>7.3f}  {notes}"
        )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Standalone Podish.CoreMark performance runner. Not part of pytest."
    )
    parser.add_argument(
        "--rootfs",
        default=str(default_rootfs()),
        help="Prepared rootfs directory from prepare_coremark_env.sh",
    )
    parser.add_argument(
        "--project-root",
        default=str(repo_root()),
        help="Repository root containing Podish.Cli",
    )
    parser.add_argument(
        "--results-dir",
        default=None,
        help="Directory for logs and JSON summary (default: benchmark/podish_perf/results/<timestamp>)",
    )
    parser.add_argument(
        "--work-dir",
        default=str(Path(__file__).resolve().parent / "work"),
        help="Temporary rootfs copy directory",
    )
    parser.add_argument(
        "--case",
        action="append",
        choices=DEFAULT_CASES,
        dest="cases",
        help="Benchmark case to run. Repeat for multiple cases; default runs all.",
    )
    parser.add_argument("--repeat", type=int, default=3, help="Samples per case")
    parser.add_argument("--iterations", type=int, default=3000, help="CoreMark iterations")
    parser.add_argument("--timeout", type=int, default=1800, help="pexpect timeout in seconds")
    parser.add_argument(
        "--reuse-rootfs",
        action="store_true",
        help="Run directly on the prepared rootfs instead of making disposable copies",
    )
    parser.add_argument(
        "--keep-workdirs",
        action="store_true",
        help="Keep copied rootfs directories after successful runs",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    project_root = Path(args.project_root).resolve()
    base_rootfs = Path(args.rootfs).resolve()
    work_dir = Path(args.work_dir).resolve()

    if not base_rootfs.is_dir():
        print(
            f"rootfs not found: {base_rootfs}\n"
            f"run benchmark/podish_perf/prepare_coremark_env.sh first",
            file=sys.stderr,
        )
        return 1

    timestamp = f"{time.strftime('%Y%m%d-%H%M%S')}-{os.getpid()}"
    results_dir = (
        Path(args.results_dir).resolve()
        if args.results_dir
        else Path(__file__).resolve().parent / "results" / timestamp
    )
    results_dir.mkdir(parents=True, exist_ok=False)
    work_dir.mkdir(parents=True, exist_ok=True)

    selected_cases = args.cases or list(DEFAULT_CASES)
    all_results: list[SampleResult] = []

    print(f"[runner] project_root={project_root}")
    print(f"[runner] rootfs={base_rootfs}")
    print(f"[runner] results_dir={results_dir}")
    print(f"[runner] cases={','.join(selected_cases)} repeat={args.repeat} iterations={args.iterations}")

    for case in selected_cases:
        for iteration in range(1, args.repeat + 1):
            print(f"[runner] case={case} sample={iteration}/{args.repeat}")
            sample = run_sample(
                project_root=project_root,
                base_rootfs=base_rootfs,
                case=case,
                iteration=iteration,
                timeout=args.timeout,
                iterations=args.iterations,
                work_dir=work_dir,
                results_dir=results_dir,
                reuse_rootfs=args.reuse_rootfs,
                keep_workdirs=args.keep_workdirs,
            )
            all_results.append(sample)
            extra = ""
            if sample.coremark_score is not None:
                extra = f" iterations/sec={sample.coremark_score:.2f}"
            print(f"[runner]   {sample.seconds:.3f}s{extra}")

    summary_path = results_dir / "summary.json"
    payload = {
        "rootfs": str(base_rootfs),
        "repeat": args.repeat,
        "iterations": args.iterations,
        "cases": selected_cases,
        "results": [asdict(result) for result in all_results],
    }
    summary_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    print_summary(all_results)
    print("")
    print(f"[runner] summary={summary_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
from dataclasses import asdict, dataclass
from pathlib import Path

import pexpect

MARKER_BEGIN = "__PODISH_BENCH_BEGIN__"
MARKER_END = "__PODISH_BENCH_END__"
DEFAULT_CASES = ("compress", "compile", "run")
DEFAULT_ENGINE = "jit"
AOT_BINARY_RELATIVE = Path("build/nativeaot/podish-cli-static/Podish.Cli")
JIT_BINARY_RELATIVE = Path("Podish.Cli/bin")


@dataclass
class SampleResult:
    engine: str
    case: str
    iteration: int
    seconds: float
    transcript: str
    work_rootfs: str
    coremark_score: float | None = None
    guest_stats_dir: str | None = None
    blocks_analysis_json: str | None = None


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def default_rootfs() -> Path:
    return Path(__file__).resolve().parent / "rootfs" / "coremark_i386_alpine"


def default_aot_binary(project_root: Path) -> Path:
    return project_root / AOT_BINARY_RELATIVE


def default_jit_configuration(project_root: Path) -> str:
    release_binary = project_root / JIT_BINARY_RELATIVE / "Release" / "net10.0" / "Podish.Cli"
    debug_binary = project_root / JIT_BINARY_RELATIVE / "Debug" / "net10.0" / "Podish.Cli"
    if release_binary.exists():
        return "Release"
    if debug_binary.exists():
        return "Debug"
    return "Release"


def default_fibercpu_library(project_root: Path) -> Path:
    host_dir = project_root / "Fiberish.X86" / "build_native" / "host"
    for name in ("libfibercpu.dylib", "libfibercpu.so", "fibercpu.dll"):
        candidate = host_dir / name
        if candidate.exists():
            return candidate
    return host_dir / "libfibercpu.dylib"


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


def build_engine_command(
    project_root: Path,
    engine: str,
    aot_binary: Path,
    rootfs: Path,
    script: str,
    jit_configuration: str = "Release",
    guest_stats_dir: Path | None = None,
) -> tuple[str, list[str]]:
    podish_args = [
        "run",
        "--rm",
        "--rootfs",
        str(rootfs),
        "--",
        "/bin/sh",
        "-lc",
        script,
    ]
    if guest_stats_dir is not None:
        podish_args[1:1] = ["--guest-stats-dir", str(guest_stats_dir)]

    if engine == "jit":
        return (
            "dotnet",
            [
                "run",
                "--project",
                str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
                "-c",
                jit_configuration,
                "--no-build",
                "--",
                *podish_args,
            ],
        )

    if engine == "aot":
        return (str(aot_binary), podish_args)

    raise ValueError(f"unknown engine: {engine}")


def clean_env() -> dict[str, str]:
    env = {
        "TERM": os.environ.get("TERM", "xterm"),
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "DOTNET_CLI_HOME": os.path.expanduser("~"),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "true",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_NOLOGO": "true",
    }
    if "PODISH_GUEST_STATS_DEBUG" in os.environ:
        env["PODISH_GUEST_STATS_DEBUG"] = os.environ["PODISH_GUEST_STATS_DEBUG"]
    return env


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


def ensure_jit_handler_profile_build(project_root: Path, disable_superopcodes: bool) -> None:
    cmd = [
        "dotnet",
        "build",
        str(project_root / "Podish.Cli" / "Podish.Cli.csproj"),
        "-c",
        "Release",
        "-p:EnableHandlerProfile=true",
    ]
    if disable_superopcodes:
        cmd.append("-p:EnableSuperOpcodes=false")
    print(f"[runner] building handler-profile JIT: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=str(project_root), env=clean_env(), check=False)
    if result.returncode != 0:
        raise RuntimeError(f"handler-profile build failed with exit code {result.returncode}")


def run_block_analysis(project_root: Path, guest_stats_dir: Path, fibercpu_library: Path) -> Path:
    return run_block_analysis_with_options(project_root, guest_stats_dir, fibercpu_library, n_gram=0, top_ngrams=100)


def run_block_analysis_with_options(
    project_root: Path,
    guest_stats_dir: Path,
    fibercpu_library: Path,
    n_gram: int,
    top_ngrams: int,
) -> Path:
    analysis_script = project_root / "scripts" / "analyze_blocks.py"
    output_path = guest_stats_dir / "blocks_analysis.json"
    cmd = [
        sys.executable,
        str(analysis_script),
        str(guest_stats_dir),
        str(fibercpu_library),
        "--output",
        str(output_path),
    ]
    if n_gram > 0:
        cmd.extend(["--n-gram", str(n_gram), "--top-ngrams", str(top_ngrams)])
    print(f"[runner] analyzing block dump: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=str(project_root), env=clean_env(), check=False)
    if result.returncode != 0:
        raise RuntimeError(f"block analysis failed with exit code {result.returncode}")
    return output_path


def run_superopcode_aggregation(
    project_root: Path,
    input_dir: Path,
    output_json: Path,
    output_md: Path,
    n_gram: int,
    top_candidates: int,
) -> tuple[Path, Path]:
    script = project_root / "benchmark" / "podish_perf" / "analyze_superopcode_candidates.py"
    cmd = [
        sys.executable,
        str(script),
        str(input_dir),
        "--n-gram",
        str(n_gram),
        "--top",
        str(top_candidates),
        "--output-json",
        str(output_json),
        "--output-md",
        str(output_md),
    ]
    print(f"[runner] aggregating superopcode candidates: {' '.join(cmd)}")
    result = subprocess.run(cmd, cwd=str(project_root), env=clean_env(), check=False)
    if result.returncode != 0:
        raise RuntimeError(f"superopcode aggregation failed with exit code {result.returncode}")
    return output_json, output_md


def run_sample(
    project_root: Path,
    engine: str,
    aot_binary: Path,
    base_rootfs: Path,
    case: str,
    iteration: int,
    timeout: int,
    iterations: int,
    work_dir: Path,
    results_dir: Path,
    reuse_rootfs: bool,
    keep_workdirs: bool,
    export_block_dump: bool,
    auto_analyze_block_dump: bool,
    fibercpu_library: Path | None,
    block_n_gram: int,
    block_top_ngrams: int,
    jit_configuration: str,
) -> SampleResult:
    work_rootfs = create_work_rootfs(base_rootfs, case, iteration, work_dir, reuse_rootfs)
    transcript = results_dir / f"{engine}-{case}-{iteration:02d}.log"
    guest_stats_dir = None
    if export_block_dump:
        guest_stats_dir = results_dir / "guest-stats" / f"{engine}-{case}-{iteration:02d}"
        guest_stats_dir.mkdir(parents=True, exist_ok=True)
    script = build_guest_script(case, iterations)
    program, args = build_engine_command(
        project_root,
        engine,
        aot_binary,
        work_rootfs,
        script,
        jit_configuration=jit_configuration,
        guest_stats_dir=guest_stats_dir,
    )
    start = 0.0
    end = 0.0

    child = pexpect.spawn(
        program,
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

    blocks_analysis_json = None
    if auto_analyze_block_dump and guest_stats_dir is not None:
        if fibercpu_library is None:
            raise RuntimeError("auto_analyze_block_dump requires a fibercpu library path")
        blocks_analysis_json = run_block_analysis_with_options(
            project_root,
            guest_stats_dir,
            fibercpu_library,
            n_gram=block_n_gram,
            top_ngrams=block_top_ngrams,
        )

    return SampleResult(
        engine=engine,
        case=case,
        iteration=iteration,
        seconds=end - start,
        transcript=str(transcript),
        work_rootfs=str(work_rootfs),
        coremark_score=extract_coremark_score(timed_output) if case == "run" else None,
        guest_stats_dir=str(guest_stats_dir) if guest_stats_dir is not None else None,
        blocks_analysis_json=str(blocks_analysis_json) if blocks_analysis_json is not None else None,
    )


def print_summary(results: list[SampleResult]) -> None:
    grouped: dict[tuple[str, str], list[SampleResult]] = {}
    for sample in results:
        grouped.setdefault((sample.engine, sample.case), []).append(sample)

    print("")
    print("Engine  Case      Samples  Min(s)  Median(s)  Mean(s)  Notes")
    print("------  --------  -------  ------  ---------  -------  -----")
    for engine in ("jit", "aot"):
        for case in DEFAULT_CASES:
            samples = grouped.get((engine, case), [])
            if not samples:
                continue
            durations = [sample.seconds for sample in samples]
            notes = ""
            scores = [sample.coremark_score for sample in samples if sample.coremark_score is not None]
            if scores:
                notes = f"Iterations/Sec median={statistics.median(scores):.2f}"
            print(
                f"{engine:<6}  {case:<8}  {len(samples):>7}  {min(durations):>6.3f}  "
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
        "--engine",
        choices=("jit", "aot"),
        default=DEFAULT_ENGINE,
        help="Execution engine: release JIT via dotnet run, or NativeAOT binary",
    )
    parser.add_argument(
        "--aot-binary",
        default=None,
        help="Path to the NativeAOT Podish.Cli binary used when --engine=aot",
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
    parser.add_argument(
        "--jit-handler-profile-block-dump",
        action="store_true",
        help="For --engine=jit, build with EnableHandlerProfile=true and export guest block dumps per sample",
    )
    parser.add_argument(
        "--block-n-gram",
        type=int,
        default=0,
        help="When exporting block dumps, also analyze N-grams of handler sequences (default: 0, disabled)",
    )
    parser.add_argument(
        "--block-top-ngrams",
        type=int,
        default=100,
        help="Maximum number of top N-gram entries to emit per sample (default: 100)",
    )
    parser.add_argument(
        "--aggregate-superopcode-candidates",
        action="store_true",
        help="After block analysis, aggregate per-sample N-grams into a candidate manifest",
    )
    parser.add_argument(
        "--disable-superopcodes",
        action="store_true",
        help="Build the JIT binary with EnableSuperOpcodes=false before running samples",
    )
    parser.add_argument(
        "--allow-superopcodes-in-block-analysis",
        action="store_true",
        help="Keep EnableSuperOpcodes=true even when exporting/analyzing JIT block dumps",
    )
    parser.add_argument(
        "--candidate-top",
        type=int,
        default=100,
        help="Maximum number of aggregate superopcode candidates to emit (default: 100)",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    project_root = Path(args.project_root).resolve()
    base_rootfs = Path(args.rootfs).resolve()
    work_dir = Path(args.work_dir).resolve()
    fibercpu_library = default_fibercpu_library(project_root)
    jit_configuration = default_jit_configuration(project_root)
    aot_binary = (
        Path(args.aot_binary).resolve()
        if args.aot_binary
        else default_aot_binary(project_root)
    )

    if not base_rootfs.is_dir():
        print(
            f"rootfs not found: {base_rootfs}\n"
            f"run benchmark/podish_perf/prepare_coremark_env.sh first",
            file=sys.stderr,
        )
        return 1
    if args.engine == "aot" and not aot_binary.is_file():
        print(
            f"aot binary not found: {aot_binary}\n"
            f"build it first with: dotnet publish Podish.Cli/Podish.Cli.csproj -c Release -r osx-arm64 -p:PublishAot=true",
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
    print(f"[runner] engine={args.engine}")
    if args.engine == "jit":
        print(f"[runner] jit_configuration={jit_configuration}")
    if args.engine == "aot":
        print(f"[runner] aot_binary={aot_binary}")
    disable_superopcodes_for_run = args.disable_superopcodes
    if args.jit_handler_profile_block_dump and not args.allow_superopcodes_in_block_analysis:
        disable_superopcodes_for_run = True
    if args.jit_handler_profile_block_dump:
        print("[runner] jit_handler_profile_block_dump=enabled")
        print(f"[runner] fibercpu_library={fibercpu_library}")
        if disable_superopcodes_for_run:
            print("[runner] disable_superopcodes=enabled")
        if not args.disable_superopcodes and disable_superopcodes_for_run:
            print("[runner] auto_disable_superopcodes_for_block_analysis=enabled")
        if args.block_n_gram > 0:
            print(f"[runner] block_n_gram={args.block_n_gram} top_ngrams={args.block_top_ngrams}")
        if args.aggregate_superopcode_candidates:
            print(f"[runner] aggregate_superopcode_candidates=enabled top={args.candidate_top}")
    print(f"[runner] cases={','.join(selected_cases)} repeat={args.repeat} iterations={args.iterations}")

    if args.jit_handler_profile_block_dump:
        if args.engine != "jit":
            print("--jit-handler-profile-block-dump requires --engine=jit", file=sys.stderr)
            return 1
        ensure_jit_handler_profile_build(project_root, disable_superopcodes=disable_superopcodes_for_run)

    for case in selected_cases:
        for iteration in range(1, args.repeat + 1):
            print(f"[runner] case={case} sample={iteration}/{args.repeat}")
            sample = run_sample(
                project_root=project_root,
                engine=args.engine,
                aot_binary=aot_binary,
                base_rootfs=base_rootfs,
                case=case,
                iteration=iteration,
                timeout=args.timeout,
                iterations=args.iterations,
                work_dir=work_dir,
                results_dir=results_dir,
                reuse_rootfs=args.reuse_rootfs,
                keep_workdirs=args.keep_workdirs,
                export_block_dump=args.jit_handler_profile_block_dump,
                auto_analyze_block_dump=args.jit_handler_profile_block_dump,
                fibercpu_library=fibercpu_library,
                block_n_gram=args.block_n_gram,
                block_top_ngrams=args.block_top_ngrams,
                jit_configuration=jit_configuration,
            )
            all_results.append(sample)
            extra = ""
            if sample.coremark_score is not None:
                extra = f" iterations/sec={sample.coremark_score:.2f}"
            print(f"[runner]   {sample.seconds:.3f}s{extra}")

    summary_path = results_dir / "summary.json"
    payload = {
        "engine": args.engine,
        "rootfs": str(base_rootfs),
        "aot_binary": str(aot_binary) if args.engine == "aot" else None,
        "repeat": args.repeat,
        "iterations": args.iterations,
        "cases": selected_cases,
        "results": [asdict(result) for result in all_results],
    }
    summary_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    if args.aggregate_superopcode_candidates:
        if not args.jit_handler_profile_block_dump:
            print("--aggregate-superopcode-candidates requires --jit-handler-profile-block-dump", file=sys.stderr)
            return 1
        if args.block_n_gram <= 0:
            print("--aggregate-superopcode-candidates requires --block-n-gram > 0", file=sys.stderr)
            return 1
        guest_stats_root = results_dir / "guest-stats"
        output_json = results_dir / "superopcode_candidates.json"
        output_md = results_dir / "superopcode_candidates.md"
        run_superopcode_aggregation(
            project_root=project_root,
            input_dir=guest_stats_root,
            output_json=output_json,
            output_md=output_md,
            n_gram=args.block_n_gram,
            top_candidates=args.candidate_top,
        )

    print_summary(all_results)
    print("")
    print(f"[runner] summary={summary_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_STATS_DIR = REPO_ROOT / "benchmark" / "podish_perf" / "results" / "20260318-151350-superopcode" / "guest-stats"
DEFAULT_GENERATED = REPO_ROOT / "libfibercpu" / "generated" / "superopcodes.generated.cpp"
DEFAULT_RESULTS_DIR = REPO_ROOT / "benchmark" / "podish_perf" / "results"


@dataclass(frozen=True)
class Strategy:
    name: str
    analyzer_args: tuple[str, ...]
    note: str
    analyzer_mode: str = "current"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Search superopcode candidate scoring strategies and keep the fastest result."
    )
    parser.add_argument("--stats-dir", default=str(DEFAULT_STATS_DIR), help="Path to guest-stats or blocks_analysis inputs")
    parser.add_argument("--candidate-top", type=int, default=256, help="Candidate count to emit")
    parser.add_argument("--superopcode-top", type=int, default=256, help="Generated superopcode count")
    parser.add_argument("--quick-repeats", type=int, default=1, help="Benchmark repeats for the first sweep")
    parser.add_argument("--finalists", type=int, default=2, help="How many top quick strategies to re-run")
    parser.add_argument("--final-repeats", type=int, default=2, help="Benchmark repeats for finalist confirmation")
    parser.add_argument("--results-dir", default=None, help="Optional directory for tuner outputs")
    return parser.parse_args()


def run(cmd: list[str], cwd: Path, *, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(cmd, cwd=str(cwd), env=env, text=True, capture_output=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(
            f"command failed with exit code {result.returncode}: {' '.join(cmd)}\n"
            f"stdout:\n{result.stdout}\n\nstderr:\n{result.stderr}"
        )
    return result


def write_previous_analyzer(temp_dir: Path) -> Path:
    content = subprocess.check_output(
        ["git", "show", "HEAD~1:benchmark/podish_perf/analyze_superopcode_candidates.py"],
        cwd=str(REPO_ROOT),
        text=True,
    )
    path = temp_dir / "analyze_superopcode_candidates_head_prev.py"
    path.write_text(content, encoding="utf-8")
    return path


def benchmark_command() -> str:
    rootfs = REPO_ROOT / "benchmark" / "podish_perf" / "rootfs" / "coremark_i386_alpine"
    return (
        "export DOTNET_CLI_TELEMETRY_OPTOUT=1; "
        "/usr/bin/time -p dotnet run --project Podish.Cli/Podish.Cli.csproj -c Release --no-build --no-restore "
        "-- run --rm -it --rootfs "
        f"{rootfs} -- /bin/sh -lc "
        "\"/coremark/coremark.exe 0x0 0x0 0x66 2000 >/dev/null 2>&1; "
        "/coremark/coremark.exe 0x0 0x0 0x66 30000\""
    )


def parse_real_seconds(output: str) -> float:
    for line in output.splitlines():
        if line.startswith("real "):
            return float(line.split()[1])
    raise RuntimeError(f"failed to parse wall time from output:\n{output}")


def benchmark_once() -> tuple[float, str]:
    result = run(
        ["/bin/zsh", "-lc", benchmark_command()],
        cwd=REPO_ROOT,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )
    combined = result.stdout + result.stderr
    return parse_real_seconds(combined), combined


def build_release() -> None:
    run(
        ["dotnet", "build", "Podish.Cli/Podish.Cli.csproj", "-c", "Release", "--no-restore"],
        cwd=REPO_ROOT,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )


def generate_candidates(
    strategy: Strategy,
    analyzer_path: Path,
    stats_dir: Path,
    out_dir: Path,
    candidate_top: int,
) -> Path:
    candidate_json = out_dir / f"{strategy.name}.candidates.json"
    candidate_md = out_dir / f"{strategy.name}.candidates.md"
    cmd = [
        sys.executable,
        str(analyzer_path),
        str(stats_dir),
        "--n-gram",
        "2",
        "--top",
        str(candidate_top),
        "--output-json",
        str(candidate_json),
        "--output-md",
        str(candidate_md),
        *strategy.analyzer_args,
    ]
    script_dir = REPO_ROOT / "scripts"
    pythonpath_parts = [str(script_dir)]
    existing_pythonpath = os.environ.get("PYTHONPATH")
    if existing_pythonpath:
        pythonpath_parts.append(existing_pythonpath)
    run(
        cmd,
        cwd=REPO_ROOT,
        env={**os.environ, "PYTHONPATH": os.pathsep.join(pythonpath_parts)},
    )
    return candidate_json


def generate_superopcodes(candidate_json: Path, superopcode_top: int) -> None:
    run(
        [
            sys.executable,
            "scripts/gen_superopcodes.py",
            "--input",
            str(candidate_json),
            "--output",
            str(DEFAULT_GENERATED),
            "--top",
            str(superopcode_top),
        ],
        cwd=REPO_ROOT,
    )


def measure_strategy(
    strategy: Strategy,
    analyzer_path: Path,
    stats_dir: Path,
    out_dir: Path,
    candidate_top: int,
    superopcode_top: int,
    repeats: int,
) -> dict[str, object]:
    candidate_json = generate_candidates(strategy, analyzer_path, stats_dir, out_dir, candidate_top)
    generate_superopcodes(candidate_json, superopcode_top)
    build_release()

    runs: list[float] = []
    outputs: list[str] = []
    for _ in range(repeats):
        seconds, output = benchmark_once()
        runs.append(seconds)
        outputs.append(output)

    return {
        "name": strategy.name,
        "note": strategy.note,
        "analyzer_mode": strategy.analyzer_mode,
        "candidate_json": str(candidate_json),
        "runs": runs,
        "best": min(runs),
        "mean": sum(runs) / len(runs),
        "raw_outputs": outputs,
    }


def strategy_list() -> list[Strategy]:
    return [
        Strategy("pair_raw2", ("--score-basis", "pair", "--raw-weight", "2", "--rar-weight", "1", "--waw-weight", "1"), "Pair-frequency scoring without Jcc boost"),
        Strategy("pair_raw2_jcc_raw", ("--score-basis", "pair", "--raw-weight", "2", "--rar-weight", "1", "--waw-weight", "1", "--jcc-multiplier", "2", "--jcc-mode", "raw-only"), "Pair-frequency scoring with RAW-only Jcc boost"),
        Strategy("pair_raw2_min1000", ("--score-basis", "pair", "--raw-weight", "2", "--rar-weight", "1", "--waw-weight", "1", "--min-weighted-exec-count", "1000"), "Pair-frequency scoring with a minimum pair hotness threshold"),
        Strategy("pair_raw2_min2000", ("--score-basis", "pair", "--raw-weight", "2", "--rar-weight", "1", "--waw-weight", "1", "--min-weighted-exec-count", "2000"), "Pair-frequency scoring with a stronger minimum pair hotness threshold"),
        Strategy("pair_raw3", ("--score-basis", "pair", "--raw-weight", "3", "--rar-weight", "1", "--waw-weight", "1"), "Pair-frequency scoring with stronger RAW preference"),
        Strategy("pair_raw3_min1000", ("--score-basis", "pair", "--raw-weight", "3", "--rar-weight", "1", "--waw-weight", "1", "--min-weighted-exec-count", "1000"), "Stronger RAW preference plus minimum pair hotness threshold"),
        Strategy("pair_raw2_nonraw0", ("--score-basis", "pair", "--raw-weight", "2", "--rar-weight", "0", "--waw-weight", "0"), "Pair-frequency scoring that effectively suppresses non-RAW pairs"),
        Strategy("pair_raw2_min_samples2", ("--score-basis", "pair", "--raw-weight", "2", "--rar-weight", "1", "--waw-weight", "1", "--min-samples", "2"), "Pair-frequency scoring that requires candidates to appear in both samples"),
    ]


def main() -> int:
    args = parse_args()
    stats_dir = Path(args.stats_dir).resolve()
    if not stats_dir.exists():
        raise FileNotFoundError(f"stats dir not found: {stats_dir}")

    timestamp = time.strftime("%Y%m%d-%H%M%S")
    results_dir = Path(args.results_dir).resolve() if args.results_dir else (DEFAULT_RESULTS_DIR / f"{timestamp}-superopcode-tune")
    results_dir.mkdir(parents=True, exist_ok=True)

    original_generated = DEFAULT_GENERATED.read_text(encoding="utf-8") if DEFAULT_GENERATED.exists() else None
    temp_dir = Path(tempfile.mkdtemp(prefix="superopcode-tune-", dir="/tmp"))
    prev_analyzer = write_previous_analyzer(temp_dir)
    current_analyzer = REPO_ROOT / "benchmark" / "podish_perf" / "analyze_superopcode_candidates.py"

    summary: dict[str, object] = {
        "stats_dir": str(stats_dir),
        "results_dir": str(results_dir),
        "benchmark_command": benchmark_command(),
        "baseline": None,
        "quick_results": [],
        "final_results": [],
        "winner": None,
    }

    try:
        build_release()
        baseline_seconds, baseline_output = benchmark_once()
        summary["baseline"] = {"real_seconds": baseline_seconds, "raw_output": baseline_output}

        quick_results = []
        for strategy in strategy_list():
            analyzer = current_analyzer if strategy.analyzer_mode == "current" else prev_analyzer
            result = measure_strategy(
                strategy,
                analyzer,
                stats_dir,
                results_dir,
                args.candidate_top,
                args.superopcode_top,
                args.quick_repeats,
            )
            quick_results.append(result)
        quick_results.sort(key=lambda item: float(item["mean"]))
        summary["quick_results"] = quick_results

        finalist_count = max(1, min(args.finalists, len(quick_results)))
        finalists = quick_results[:finalist_count]
        final_results = []
        for item in finalists:
            strategy = next(s for s in strategy_list() if s.name == item["name"])
            analyzer = current_analyzer if strategy.analyzer_mode == "current" else prev_analyzer
            result = measure_strategy(
                strategy,
                analyzer,
                stats_dir,
                results_dir,
                args.candidate_top,
                args.superopcode_top,
                args.final_repeats,
            )
            final_results.append(result)
        final_results.sort(key=lambda item: float(item["mean"]))
        summary["final_results"] = final_results

        winner_name = str(final_results[0]["name"]) if final_results else str(quick_results[0]["name"])
        winner = next(s for s in strategy_list() if s.name == winner_name)
        winner_analyzer = current_analyzer if winner.analyzer_mode == "current" else prev_analyzer
        winner_candidates = generate_candidates(
            winner,
            winner_analyzer,
            stats_dir,
            results_dir,
            args.candidate_top,
        )
        generate_superopcodes(winner_candidates, args.superopcode_top)
        build_release()
        summary["winner"] = {
            "name": winner.name,
            "note": winner.note,
            "analyzer_mode": winner.analyzer_mode,
            "candidate_json": str(winner_candidates),
        }

        (results_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print(json.dumps(summary["winner"], indent=2))
        print(f"baseline_real={baseline_seconds:.2f}s")
        if final_results:
            print(f"winner_mean={float(final_results[0]['mean']):.2f}s")
        return 0
    finally:
        if summary.get("winner") is None and original_generated is not None:
            DEFAULT_GENERATED.write_text(original_generated, encoding="utf-8")
        shutil.rmtree(temp_dir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())

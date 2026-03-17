#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable


DEFAULT_TIME_LIMIT = 18
DEFAULT_WARMUP_SECONDS = 4.0
DEFAULT_TOP = 25
DEFAULT_DISASM_SYMBOLS = 8
DEFAULT_RECORD_NAME = "coremark-profile"
DEFAULT_AOT_BINARY = Path("build/nativeaot/podish-cli-static/Podish.Cli")
DEFAULT_ROOTFS = Path("benchmark/podish_perf/rootfs/coremark_i386_alpine")
DEFAULT_RENAMED_BINARY = "PodishCliXcTraceProfile"
DEFAULT_BENCH_CASE = "run"
DEFAULT_AOT_RUNTIME = "osx-arm64"


@dataclass
class Hotspot:
    rank: int
    symbol: str
    self_ms: float
    sample_count: int
    binary_name: str | None


@dataclass
class ReportMetadata:
    trace_path: str
    xml_path: str
    binary_path: str
    warmup_seconds: float
    total_rows: int
    kept_rows: int


@dataclass
class SymbolEntry:
    address: str
    mangled: str
    demangled: str
    next_address: str | None = None


@dataclass
class JitOpEntry:
    index: int
    name: str
    runtime_start: int
    offset: int


@dataclass
class JitBlockEntry:
    guest_block_start_eip: int
    runtime_start: int
    code_size: int
    ops: list[JitOpEntry]


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def default_binary() -> Path:
    return repo_root() / DEFAULT_AOT_BINARY


def default_rootfs() -> Path:
    return repo_root() / DEFAULT_ROOTFS


def results_dir() -> Path:
    return repo_root() / "benchmark" / "podish_perf" / "results"


def refresh_default_binary(binary: Path) -> None:
    default = default_binary().resolve()
    if binary.resolve() != default:
        return

    project_root = repo_root()
    run_checked(
        [
            "dotnet",
            "publish",
            "Podish.Cli/Podish.Cli.csproj",
            "-c",
            "Release",
            "-r",
            DEFAULT_AOT_RUNTIME,
            "-p:PublishAot=true",
            "-o",
            str(default.parent),
        ],
        cwd=project_root,
    )


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
tar -C / -cf /tmp/coremark.tar coremark
gzip -1 -c /tmp/coremark.tar > /tmp/coremark.tar.gz
gzip -dc /tmp/coremark.tar.gz > /tmp/coremark-restored.tar
tar -C /tmp/coremark-unpack -xf /tmp/coremark-restored.tar
test -f /tmp/coremark-unpack/coremark/Makefile
"""
    if case in ("compile", "gcc_compile"):
        return f"""
set -eu
cd /coremark
make clean >/dev/null 2>&1 || true
sync >/dev/null 2>&1 || true
{compile_cmd}
test -x /coremark/coremark.exe
"""
    if case == "run":
        return f"""
set -eu
cd /coremark
test -x ./coremark.exe || {compile_cmd} >/dev/null
/coremark/coremark.exe 0x0 0x0 0x66 2000 >/dev/null 2>&1
./coremark.exe 0x0 0x0 0x66 {iterations}
"""
    raise ValueError(f"unknown bench case: {case}")


def default_guest_command(case: str, iterations: int) -> list[str]:
    return ["/bin/sh", "-lc", build_guest_script(case, iterations)]


def shell_join(parts: Iterable[str]) -> str:
    import shlex

    return " ".join(shlex.quote(part) for part in parts)


def run_checked(
    cmd: list[str],
    cwd: Path | None = None,
    capture: bool = False,
    ok_exit_codes: tuple[int, ...] = (0,),
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        cmd,
        cwd=str(cwd) if cwd else None,
        text=True,
        capture_output=capture,
    )
    if result.returncode not in ok_exit_codes:
        raise subprocess.CalledProcessError(result.returncode, cmd, output=result.stdout, stderr=result.stderr)
    return result


def run_xctrace_record(cmd: list[str], trace_path: Path, env: dict[str, str] | None = None) -> None:
    result = subprocess.run(cmd, text=True, capture_output=False, env=env)
    if result.returncode == 0:
        return
    if trace_path.exists():
        return
    raise subprocess.CalledProcessError(result.returncode, cmd)


def make_output_dir(base: Path, name: str) -> Path:
    out = base / name
    out.mkdir(parents=True, exist_ok=True)
    return out


def make_unique_binary(src_binary: Path, out_dir: Path, renamed_binary: str) -> Path:
    for sibling in src_binary.parent.iterdir():
        if not sibling.is_file():
            continue
        if sibling.name == src_binary.name:
            continue
        shutil.copy2(sibling, out_dir / sibling.name)

    dst = out_dir / renamed_binary
    shutil.copy2(src_binary, dst)
    dst.chmod(dst.stat().st_mode | 0o111)
    return dst


def build_record_command(
    binary: Path,
    rootfs: Path,
    time_limit: int,
    output_trace: Path,
    iterations: int,
    bench_case: str,
) -> list[str]:
    guest_cmd = default_guest_command(bench_case, iterations)
    return [
        "xcrun",
        "xctrace",
        "record",
        "--template",
        "Time Profiler",
        "--time-limit",
        f"{time_limit}s",
        "--output",
        str(output_trace),
        "--launch",
        "--",
        str(binary),
        "run",
        "--rm",
        "--rootfs",
        str(rootfs),
        "--",
        *guest_cmd,
    ]


def export_time_profile(trace_path: Path, xml_path: Path) -> None:
    xpath = '/trace-toc/run/data/table[@schema="time-profile"]'
    cmd = [
        "xcrun",
        "xctrace",
        "export",
        "--input",
        str(trace_path),
        "--xpath",
        xpath,
    ]
    result = run_checked(cmd, capture=True)
    xml_path.write_text(result.stdout, encoding="utf-8")


def build_id_index(root: ET.Element) -> dict[str, ET.Element]:
    index: dict[str, ET.Element] = {}
    for element in root.iter():
        element_id = element.attrib.get("id")
        if element_id:
            index[element_id] = element
    return index


def resolve_ref(element: ET.Element | None, id_index: dict[str, ET.Element]) -> ET.Element | None:
    if element is None:
        return None
    ref = element.attrib.get("ref")
    if ref:
        return id_index.get(ref)
    return element


def element_text(element: ET.Element | None, id_index: dict[str, ET.Element]) -> str | None:
    resolved = resolve_ref(element, id_index)
    if resolved is None:
        return None
    return resolved.text


def parse_hex_symbol(symbol: str) -> int | None:
    if not symbol.startswith("0x"):
        return None
    try:
        return int(symbol, 16)
    except ValueError:
        return None


def load_jit_profile_maps(map_dir: Path | None) -> list[JitBlockEntry]:
    if map_dir is None or not map_dir.exists():
        return []
    entries: list[JitBlockEntry] = []
    for path in sorted(map_dir.glob("jit_*.map.json")):
        obj = json.loads(path.read_text(encoding="utf-8"))
        ops = [
            JitOpEntry(
                index=int(op["index"]),
                name=str(op["name"]),
                runtime_start=int(op["runtime_start"]),
                offset=int(op["offset"]),
            )
            for op in obj.get("ops", [])
        ]
        entries.append(
            JitBlockEntry(
                guest_block_start_eip=int(obj["guest_block_start_eip"]),
                runtime_start=int(obj["runtime_start"]),
                code_size=int(obj["code_size"]),
                ops=ops,
            )
        )
    entries.sort(key=lambda item: item.runtime_start)
    return entries


def annotate_jit_symbol(symbol: str, jit_maps: list[JitBlockEntry]) -> tuple[str, str | None]:
    addr = parse_hex_symbol(symbol)
    if addr is None:
        return symbol, None
    for block in jit_maps:
        if not (block.runtime_start <= addr < block.runtime_start + block.code_size):
            continue
        block_off = addr - block.runtime_start
        current_op: JitOpEntry | None = None
        for op in block.ops:
            if op.runtime_start <= addr:
                current_op = op
            else:
                break
        if current_op is None:
            return f"jit:block@{block.guest_block_start_eip:08x}+0x{block_off:x}", "jit"
        op_off = addr - current_op.runtime_start
        return (
            f"jit:block@{block.guest_block_start_eip:08x}+0x{block_off:x} "
            f"op[{current_op.index}] {current_op.name}+0x{op_off:x}",
            "jit",
        )
    return symbol, None


def parse_time_profile(
    xml_path: Path, warmup_seconds: float, jit_maps: list[JitBlockEntry] | None = None
) -> tuple[list[Hotspot], ReportMetadata]:
    root = ET.fromstring(xml_path.read_text(encoding="utf-8"))
    id_index = build_id_index(root)
    rows = root.findall(".//row")
    aggregated: dict[tuple[str, str | None], float] = defaultdict(float)
    counts: dict[tuple[str, str | None], int] = defaultdict(int)
    total_rows = 0
    kept_rows = 0
    warmup_ns = int(warmup_seconds * 1_000_000_000)
    jit_maps = jit_maps or []

    def first_interesting_frame(backtrace: ET.Element | None) -> ET.Element | None:
        if backtrace is None:
            return None
        frames = backtrace.findall("frame")
        if not frames:
            return None
        resolved_frames = [resolve_ref(frame, id_index) for frame in frames]
        resolved_frames = [frame for frame in resolved_frames if frame is not None]
        if not resolved_frames:
            return None
        for frame in resolved_frames:
            name = frame.attrib.get("name", "")
            if name and name != "<deduplicated_symbol>":
                return frame
        return resolved_frames[0]

    for row in rows:
        total_rows += 1
        sample_time = element_text(row.find("sample-time"), id_index)
        weight = element_text(row.find("weight"), id_index)
        backtrace = resolve_ref(row.find("backtrace"), id_index)
        frame = first_interesting_frame(backtrace)
        if sample_time is None or weight is None or frame is None:
            continue
        if int(sample_time) < warmup_ns:
            continue
        symbol = frame.attrib.get("name", "<unknown>")
        binary = resolve_ref(frame.find("binary"), id_index)
        binary_name = binary.attrib.get("name") if binary is not None else None
        if binary_name is None:
            symbol, annotated_binary = annotate_jit_symbol(symbol, jit_maps)
            if annotated_binary is not None:
                binary_name = annotated_binary
        key = (symbol, binary_name)
        aggregated[key] += int(weight) / 1_000_000.0
        counts[key] += 1
        kept_rows += 1

    hotspots: list[Hotspot] = []
    for rank, ((symbol, binary_name), self_ms) in enumerate(
        sorted(aggregated.items(), key=lambda item: item[1], reverse=True),
        start=1,
    ):
        hotspots.append(
            Hotspot(
                rank=rank,
                symbol=symbol,
                self_ms=round(self_ms, 3),
                sample_count=counts[(symbol, binary_name)],
                binary_name=binary_name,
            )
        )

    metadata = ReportMetadata(
        trace_path="",
        xml_path=str(xml_path),
        binary_path="",
        warmup_seconds=warmup_seconds,
        total_rows=total_rows,
        kept_rows=kept_rows,
    )
    return hotspots, metadata


def disassemble_symbols(binary: Path, symbols: list[str], out_dir: Path) -> dict[str, str]:
    symbol_index = load_symbol_index(binary)
    result: dict[str, str] = {}
    for symbol in symbols:
        resolved = resolve_symbol_name(symbol, symbol_index)
        if resolved is None:
            continue
        safe_name = re.sub(r"[^A-Za-z0-9_.-]+", "_", symbol)[:120]
        out_path = out_dir / f"disasm-{safe_name}.txt"
        cmd = [
            "xcrun",
            "llvm-objdump",
            "--demangle",
            "--disassemble",
            f"--start-address=0x{resolved.address}",
            str(binary),
        ]
        if resolved.next_address is not None:
            cmd.insert(-1, f"--stop-address=0x{resolved.next_address}")
        completed = run_checked(cmd, capture=True)
        out_path.write_text(completed.stdout, encoding="utf-8")
        result[symbol] = str(out_path)
    return result


def parse_nm_output(text: str) -> dict[str, tuple[str, str]]:
    pattern = re.compile(r"^([0-9a-fA-F]+)\s+\S\s+(.+)$")
    mapping: dict[str, tuple[str, str]] = {}
    for line in text.splitlines():
        match = pattern.match(line.strip())
        if not match:
            continue
        address, name = match.groups()
        mapping[address] = (address, name)
    return mapping


def load_symbol_index(binary: Path) -> dict[str, SymbolEntry]:
    mangled_out = run_checked(["xcrun", "llvm-nm", "-n", str(binary)], capture=True).stdout
    demangled_out = run_checked(["xcrun", "llvm-nm", "-C", "-n", str(binary)], capture=True).stdout
    mangled = parse_nm_output(mangled_out)
    demangled = parse_nm_output(demangled_out)
    ordered_addresses = sorted(demangled, key=lambda value: int(value, 16))
    index: dict[str, SymbolEntry] = {}
    for i, address in enumerate(ordered_addresses):
        demangled_name = demangled[address][1]
        mangled_name = mangled.get(address, ("", demangled_name))[1]
        next_address = ordered_addresses[i + 1] if i + 1 < len(ordered_addresses) else None
        index[demangled_name] = SymbolEntry(
            address=address,
            mangled=mangled_name,
            demangled=demangled_name,
            next_address=next_address,
        )
    return index


def resolve_symbol_name(symbol: str, index: dict[str, SymbolEntry]) -> SymbolEntry | None:
    direct = index.get(symbol)
    if direct is not None:
        return direct
    for demangled_name, entry in index.items():
        if demangled_name.endswith(symbol) or symbol.endswith(demangled_name):
            return entry
    return None


def write_report(
    out_dir: Path,
    report_name: str,
    trace_path: Path,
    xml_path: Path,
    binary_path: Path,
    hotspots: list[Hotspot],
    warmup_seconds: float,
    total_rows: int,
    kept_rows: int,
    disassembly: dict[str, str],
) -> tuple[Path, Path]:
    report_json = out_dir / f"{report_name}.report.json"
    report_md = out_dir / f"{report_name}.report.md"
    payload = {
        "metadata": asdict(
            ReportMetadata(
                trace_path=str(trace_path),
                xml_path=str(xml_path),
                binary_path=str(binary_path),
                warmup_seconds=warmup_seconds,
                total_rows=total_rows,
                kept_rows=kept_rows,
            )
        ),
        "hotspots": [asdict(h) for h in hotspots],
        "disassembly": disassembly,
    }
    report_json.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    lines = [
        f"# {report_name}",
        "",
        f"- trace: `{trace_path}`",
        f"- xml: `{xml_path}`",
        f"- binary: `{binary_path}`",
        f"- warmup cutoff: `{warmup_seconds:.1f}s`",
        f"- kept samples: `{kept_rows}/{total_rows}`",
        "",
        "| Rank | Self ms | Samples | Symbol | Binary |",
        "|---:|---:|---:|---|---|",
    ]
    for hotspot in hotspots:
        lines.append(
            f"| {hotspot.rank} | {hotspot.self_ms:.3f} | {hotspot.sample_count} | "
            f"`{hotspot.symbol}` | `{hotspot.binary_name or ''}` |"
        )

    if disassembly:
        lines.extend(["", "## Disassembly", ""])
        for symbol, path in disassembly.items():
            lines.append(f"- `{symbol}`: `{path}`")

    report_md.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return report_json, report_md


def load_report(report_json: Path) -> dict:
    return json.loads(report_json.read_text(encoding="utf-8"))


def compare_reports(report_paths: list[Path], top: int, out_path: Path | None) -> str:
    reports = [load_report(path) for path in report_paths]
    labels = [path.stem.replace(".report", "") for path in report_paths]
    per_report = []
    all_symbols: set[str] = set()
    for report in reports:
        mapping = {item["symbol"]: item for item in report["hotspots"]}
        per_report.append(mapping)
        all_symbols.update(mapping)

    def total_ms(symbol: str) -> float:
        return max(mapping.get(symbol, {}).get("self_ms", 0.0) for mapping in per_report)

    ranked_symbols = sorted(all_symbols, key=total_ms, reverse=True)[:top]
    lines = [
        "# hotspot comparison",
        "",
        "| Symbol | " + " | ".join(f"{label} (ms)" for label in labels) + " |",
        "|---|" + "|".join("---:" for _ in labels) + "|",
    ]
    for symbol in ranked_symbols:
        values = [mapping.get(symbol, {}).get("self_ms", 0.0) for mapping in per_report]
        lines.append("| `{}` | {} |".format(symbol, " | ".join(f"{value:.3f}" for value in values)))
    content = "\n".join(lines) + "\n"
    if out_path:
        out_path.write_text(content, encoding="utf-8")
    return content


def cmd_record(args: argparse.Namespace) -> int:
    src_binary = args.binary.resolve()
    refresh_default_binary(src_binary)
    rootfs = args.rootfs.resolve()
    out_dir = make_output_dir(args.output_dir.resolve(), args.name)
    run_binary = make_unique_binary(src_binary, out_dir, args.renamed_binary)
    trace_path = out_dir / f"{args.name}.trace"
    if args.jit_map_dir:
        args.jit_map_dir.mkdir(parents=True, exist_ok=True)
    cmd = build_record_command(run_binary, rootfs, args.time_limit, trace_path, args.iterations, args.bench_case)
    (out_dir / "record-command.txt").write_text(shell_join(cmd) + "\n", encoding="utf-8")
    env = os.environ.copy()
    if args.jit_map_dir:
        env["FIBERCPU_JIT_PROFILE_MAP_DIR"] = str(args.jit_map_dir.resolve())
    run_xctrace_record(cmd, trace_path, env=env)
    print(trace_path)
    return 0


def cmd_analyze(args: argparse.Namespace) -> int:
    trace_path = args.trace.resolve()
    binary_path = args.binary.resolve()
    out_dir = make_output_dir(args.output_dir.resolve(), args.name)
    xml_path = out_dir / f"{args.name}.xml"
    export_time_profile(trace_path, xml_path)
    jit_maps = load_jit_profile_maps(args.jit_map_dir.resolve()) if args.jit_map_dir else []
    hotspots, metadata = parse_time_profile(xml_path, args.warmup_seconds, jit_maps)
    hotspots = hotspots[: args.top]
    top_symbols = [hotspot.symbol for hotspot in hotspots[: args.disasm_top]]
    disassembly = disassemble_symbols(binary_path, top_symbols, out_dir) if args.disasm_top > 0 else {}
    report_json, report_md = write_report(
        out_dir,
        args.name,
        trace_path,
        xml_path,
        binary_path,
        hotspots,
        args.warmup_seconds,
        metadata.total_rows,
        metadata.kept_rows,
        disassembly,
    )
    print(report_json)
    print(report_md)
    return 0


def cmd_record_and_analyze(args: argparse.Namespace) -> int:
    src_binary = args.binary.resolve()
    refresh_default_binary(src_binary)
    rootfs = args.rootfs.resolve()
    out_dir = make_output_dir(args.output_dir.resolve(), args.name)
    run_binary = make_unique_binary(src_binary, out_dir, args.renamed_binary)
    trace_path = out_dir / f"{args.name}.trace"
    if args.jit_map_dir:
        args.jit_map_dir.mkdir(parents=True, exist_ok=True)
    cmd = build_record_command(run_binary, rootfs, args.time_limit, trace_path, args.iterations, args.bench_case)
    (out_dir / "record-command.txt").write_text(shell_join(cmd) + "\n", encoding="utf-8")
    env = os.environ.copy()
    if args.jit_map_dir:
        env["FIBERCPU_JIT_PROFILE_MAP_DIR"] = str(args.jit_map_dir.resolve())
    run_xctrace_record(cmd, trace_path, env=env)

    xml_path = out_dir / f"{args.name}.xml"
    export_time_profile(trace_path, xml_path)
    jit_maps = load_jit_profile_maps(args.jit_map_dir.resolve()) if args.jit_map_dir else []
    hotspots, metadata = parse_time_profile(xml_path, args.warmup_seconds, jit_maps)
    hotspots = hotspots[: args.top]
    top_symbols = [hotspot.symbol for hotspot in hotspots[: args.disasm_top]]
    disassembly = disassemble_symbols(run_binary, top_symbols, out_dir) if args.disasm_top > 0 else {}
    report_json, report_md = write_report(
        out_dir,
        args.name,
        trace_path,
        xml_path,
        run_binary,
        hotspots,
        args.warmup_seconds,
        metadata.total_rows,
        metadata.kept_rows,
        disassembly,
    )
    print(report_json)
    print(report_md)
    return 0


def cmd_compare(args: argparse.Namespace) -> int:
    content = compare_reports([path.resolve() for path in args.report], args.top, args.output)
    sys.stdout.write(content)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Record and analyze xctrace profiles for Podish benchmark workloads.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    def add_common_record(subparser: argparse.ArgumentParser) -> None:
        subparser.add_argument("--binary", type=Path, default=default_binary())
        subparser.add_argument("--rootfs", type=Path, default=default_rootfs())
        subparser.add_argument("--output-dir", type=Path, default=results_dir())
        subparser.add_argument("--name", default=DEFAULT_RECORD_NAME)
        subparser.add_argument("--time-limit", type=int, default=DEFAULT_TIME_LIMIT)
        subparser.add_argument("--iterations", type=int, default=30000)
        subparser.add_argument("--jit-map-dir", type=Path, help="Directory where runtime JIT block maps will be written")
        subparser.add_argument(
            "--bench-case",
            choices=("run", "compile", "compress", "gcc_compile"),
            default=DEFAULT_BENCH_CASE,
            help="Guest workload to run while recording. 'gcc_compile' is an explicit alias for the CoreMark gcc build workload.",
        )
        subparser.add_argument("--renamed-binary", default=DEFAULT_RENAMED_BINARY)

    def add_common_analyze(subparser: argparse.ArgumentParser) -> None:
        subparser.add_argument("--trace", type=Path, required=True)
        subparser.add_argument("--binary", type=Path, default=default_binary())
        subparser.add_argument("--output-dir", type=Path, default=results_dir())
        subparser.add_argument("--name", default=DEFAULT_RECORD_NAME)
        subparser.add_argument("--warmup-seconds", type=float, default=DEFAULT_WARMUP_SECONDS)
        subparser.add_argument("--top", type=int, default=DEFAULT_TOP)
        subparser.add_argument("--disasm-top", type=int, default=DEFAULT_DISASM_SYMBOLS)
        subparser.add_argument("--jit-map-dir", type=Path, help="Directory containing jit_*.map.json files")

    record_parser = subparsers.add_parser("record", help="Record a new xctrace trace.")
    add_common_record(record_parser)
    record_parser.set_defaults(func=cmd_record)

    analyze_parser = subparsers.add_parser("analyze", help="Export/analyze an existing trace.")
    add_common_analyze(analyze_parser)
    analyze_parser.set_defaults(func=cmd_analyze)

    both_parser = subparsers.add_parser("record-and-analyze", help="Record a trace and immediately analyze it.")
    add_common_record(both_parser)
    both_parser.add_argument("--warmup-seconds", type=float, default=DEFAULT_WARMUP_SECONDS)
    both_parser.add_argument("--top", type=int, default=DEFAULT_TOP)
    both_parser.add_argument("--disasm-top", type=int, default=DEFAULT_DISASM_SYMBOLS)
    both_parser.set_defaults(func=cmd_record_and_analyze)

    compare_parser = subparsers.add_parser("compare", help="Compare multiple report.json files.")
    compare_parser.add_argument("--report", type=Path, action="append", required=True)
    compare_parser.add_argument("--top", type=int, default=DEFAULT_TOP)
    compare_parser.add_argument("--output", type=Path)
    compare_parser.set_defaults(func=cmd_compare)

    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())

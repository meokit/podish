#!/usr/bin/env python3
"""Run a validated set of i386 workloads on one host."""
import argparse
import contextlib
import ctypes
import gzip
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import selectors
import shutil
import statistics
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
CASES = json.loads((HERE / "cases.json").read_text())
BACKENDS = ("podish-off", "podish-jit", "qemu-user", "qemu-system", "ish")
GUEST_ENV = {"LC_ALL": "C", "LANG": "C", "TZ": "UTC", "UV_THREADPOOL_SIZE": "1"}
FILE_RESULTS = {"sort": "sort.txt", "webpack": "bundle.js", "gzip": "gzip.bin", "gemm": "gemm.txt"}


def digest(path):
    with path.open("rb") as data:
        return hashlib.file_digest(data, "sha256").hexdigest()


def command_output(argv):
    return subprocess.check_output(argv, stderr=subprocess.STDOUT, text=True).strip()


def verify_bundle(bundle):
    expected = {
        filename: checksum
        for checksum, filename in (line.split() for line in (bundle / "SHA256SUMS").read_text().splitlines())
    }
    for filename in ("rootfs.tar.gz", "rootfs.cpio.gz", "kernel"):
        if digest(bundle / filename) != expected[filename]:
            raise ValueError(f"fixture checksum mismatch for {filename}")
    return expected


def prepare_root(archive, target, checksum, fakefsify=None):
    stamp = target.with_name(f".{target.name}.sha256")
    if target.is_dir() and stamp.is_file() and stamp.read_text().strip() == checksum:
        return
    if target.exists():
        raise ValueError(f"fixture directory already exists without the expected checksum: {target}")
    with tempfile.TemporaryDirectory(dir=target.parent) as temporary:
        unpacked = Path(temporary) / "root"
        if fakefsify:
            command = [str(fakefsify), str(archive), str(unpacked)]
        else:
            unpacked.mkdir()
            command = ["tar", "-xzf", str(archive), "-C", str(unpacked)]
            if os.getuid() != 0:
                command.insert(1, "--no-same-owner")
        subprocess.run(command, check=True)
        unpacked.rename(target)
    stamp.write_text(checksum + "\n")


def prepare_initrd(archive, target, checksum):
    # Unpack on the host so every TCG boot can consume the same raw CPIO.
    stamp = target.with_suffix(".sha256")
    if target.is_file() and stamp.is_file() and stamp.read_text().strip() == checksum:
        return
    with tempfile.TemporaryDirectory(dir=target.parent) as temporary:
        unpacked = Path(temporary) / "rootfs.cpio"
        with gzip.open(archive, "rb") as source, unpacked.open("wb") as destination:
            shutil.copyfileobj(source, destination)
        unpacked.replace(target)
    stamp.write_text(checksum + "\n")


def launcher(options, root, ish_root, case, backend):
    program, *arguments = [arg.format(iterations=options.iterations) for arg in CASES[case]]
    env = [f"{name}={value}" for name, value in GUEST_ENV.items()]
    guest_program = f"/opt/bench/{program[2:]}" if program.startswith("./") else program
    guest_command = 'cd /opt/bench && exec "$@"'
    if case == "gzip":
        guest_command += " > out/gzip.bin"
    elif case == "gemm":
        guest_command += " 2> out/gemm.txt"

    if backend.startswith("podish-"):
        jit = ["-XX:JitMode=off"] if backend == "podish-off" else [
            f"-XX:JitMode={options.jit_mode}", "-XX:JitWorkers=2",
            f"-XX:JitArenaSize={options.jit_arena}",
        ]
        return [
            "dotnet", str(options.podish), "run", "--rm", "--log-driver", "none",
            "--rootfs", str(root), "--memory", "1G",
            f"-XX:BlockCacheSize={options.block_cache}", *jit,
            *[item for assignment in env for item in ("-e", assignment)],
            "--", "/bin/sh", "-c", guest_command, "bench", guest_program, *arguments,
        ]
    if backend == "qemu-user":
        executable = root / guest_program.lstrip("/")
        return [options.qemu_user, "-cpu", options.qemu_cpu, "-L", str(root), str(executable), *arguments]
    if backend == "ish":
        return [
            str(options.ish), "-f", str(ish_root), "-d", "/opt/bench",
            "/usr/bin/env", *env, "/bin/sh", "-c", guest_command, "bench", guest_program, *arguments,
        ]
    kernel_args = (
        "console=ttyS0 quiet panic=-1 rdinit=/opt/bench/system-init "
        f"bench_case={case} bench_iterations={options.iterations}"
    )
    return [
        options.qemu_system, "-machine", "pc", "-accel", "tcg,thread=single",
        "-cpu", options.qemu_cpu, "-smp", "1", "-m", "1024",
        "-display", "none", "-serial", "stdio", "-monitor", "none",
        "-nic", "none", "-no-reboot", "-kernel", str(options.bundle / "kernel"),
        "-initrd", str(options.state / "rootfs.cpio"), "-append", kernel_args,
    ]



def run_system(argv, timeout, env):
    launched = time.monotonic()
    started = None
    ended = None
    status = None
    complete = False
    lines = []
    with subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT, env=env) as process:
        # Keep the timer on the host and delimit the workload with guest serial markers.
        with selectors.DefaultSelector() as poller:
            poller.register(process.stdout, selectors.EVENT_READ)
            pending = b""
            while True:
                remaining = launched + timeout - time.monotonic()
                if remaining <= 0 or not poller.select(remaining):
                    process.kill()
                    raise subprocess.TimeoutExpired(argv, timeout, output=b"".join(lines) + pending)
                chunk = os.read(process.stdout.fileno(), 65536)
                arrived = time.monotonic()
                if not chunk:
                    break
                pending += chunk
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", maxsplit=1)
                    lines.append(line + b"\n")
                    marker = line.strip()
                    if marker == b"BENCH_BEGIN":
                        started = arrived
                    elif marker.startswith(b"BENCH_END "):
                        ended = arrived
                        status = int(marker.split()[1])
                    elif marker == b"BENCH_FINISHED":
                        complete = True
            lines.append(pending)
        try:
            returncode = process.wait(timeout=max(0, launched + timeout - time.monotonic()))
        except subprocess.TimeoutExpired as error:
            process.kill()
            error.output = b"".join(lines)
            raise
    output = b"".join(lines)
    if returncode or not complete or started is None or ended is None or status != 0:
        raise subprocess.CalledProcessError(returncode or status or 1, argv, output=output)
    return output, b"", ended - started, started - launched


def match(pattern, text):
    result = re.search(pattern, text, re.MULTILINE)
    if result is None:
        raise ValueError(f"missing expected output matching {pattern}")
    return result.group(1)


def verify_run(case, backend, stdout, output_dir, expected, options):
    text = stdout.decode("utf-8", errors="replace").replace("\r\n", "\n")
    fields = {}
    if case == "coremark":
        compiler = match(r"^Compiler version[ \t]*:[ \t]*(.+)$", text)
        flags = match(r"^Compiler flags[ \t]*:[ \t]*(.+)$", text).split()
        optimization = [flag for flag in flags if re.fullmatch(r"-O(?:[0-3sgz]|fast)?", flag)]
        if optimization[-1:] != ["-O3"]:
            raise ValueError(f"CoreMark did not report an effective -O3: {flags}")
        build = options.coremark_build
        if build and (compiler != "GCC" + build["compiler"].split()[-1] or flags != build["flags"].split()):
            raise ValueError(f"CoreMark build does not match the fixture: {compiler} {flags}")
        fields.update(coremark_compiler=compiler, coremark_flags=flags)
        crcs = dict(re.findall(r"^(?:\[0\])?(seedcrc|crc\w+)\s*:\s*0x([\da-f]+)$", text, re.MULTILINE))
        fields["final_crc"] = crcs.pop("crcfinal", None)
        if not fields["final_crc"] or crcs != {
            "seedcrc": "e9f5", "crclist": "e714", "crcmatrix": "1fd7", "crcstate": "8e3a",
        }:
            raise ValueError(f"CoreMark CRC check failed: {crcs}")
        if int(match(r"^Iterations\s*:\s*(\d+)", text)) != options.iterations:
            raise ValueError("CoreMark reported the wrong iteration count")
        fields["score"] = float(match(r"^Iterations/Sec\s*:\s*([\d.]+)", text))
        fields["guest_seconds"] = float(match(r"^Total time \(secs\)\s*:\s*([\d.]+)", text))
        if not options.smoke and (fields["guest_seconds"] < 10 or "Correct operation validated" not in text):
            raise ValueError("CoreMark official run must last at least 10 seconds and validate")
    elif case.startswith("scimark"):
        fields["score"] = float(match(r"^Composite Score:\s*([\d.]+)", text))
        if not math.isfinite(float(match(r"^checksum:\s*(\S+)", text))) or re.search("error|invalid", text, re.I):
            raise ValueError("SciMark computation failed")
        if len(re.findall(r"Mflops:", text)) != 5:
            raise ValueError("SciMark did not complete all five kernels")
    else:
        if case == "webpack":
            actual = match(r"^BUNDLE_SHA256 ([a-f\d]{64})", text)
            if backend != "qemu-system" and actual != digest(output_dir / FILE_RESULTS[case]):
                raise ValueError("Webpack reported a hash different from the emitted bundle")
        elif case == "sha256sum" or backend == "qemu-system":
            actual = match(r"^([a-f\d]{64})\s", text)
        else:
            actual = digest(output_dir / FILE_RESULTS[case])
        expected_hash = (expected / case).read_text().split()[1 if case == "webpack" else 0]
        if actual != expected_hash:
            raise ValueError(f"{case} output mismatch: {actual} != {expected_hash}")
        fields["output_sha256"] = actual
        if case == "gemm":
            fields["guest_seconds"] = float(match(r"^([\d]+\.[\d]+)\s*$", text))
    return fields


def measure(options, root, ish_root, case, backend, round_number):
    output_dir = (ish_root / "data" if backend == "ish" else root) / "opt/bench/out"
    for filename in FILE_RESULTS.values():
        (output_dir / filename).unlink(missing_ok=True)
    argv = launcher(options, root, ish_root, case, backend)
    env = dict(
        os.environ, **{**GUEST_ENV, "FIBERCPU_LLVM_JIT": "0", "FIBERCPU_LINKED_BLOCK_CACHE": "0",
                      "FIBERCPU_DYNAMIC_SPECIALIZATION": "0", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )
    row = {"case": case, "backend": backend, "round": round_number, "argv": argv}
    stdout, stderr = b"", b""
    try:
        if backend == "qemu-system":
            stdout, stderr, elapsed, boot_seconds = run_system(argv, options.timeout, env)
            row["boot_seconds"] = boot_seconds
        else:
            start = time.monotonic()
            with contextlib.ExitStack() as files:
                stdout_target, stderr_target = subprocess.PIPE, subprocess.PIPE
                if backend == "qemu-user" and case == "gzip":
                    stdout_target = files.enter_context((output_dir / FILE_RESULTS[case]).open("wb"))
                if backend == "qemu-user" and case == "gemm":
                    stderr_target = files.enter_context((output_dir / FILE_RESULTS[case]).open("wb"))
                result = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=stdout_target, stderr=stderr_target,
                                        cwd=root / "opt/bench", env=env, timeout=options.timeout, check=True)
            elapsed = time.monotonic() - start
            stdout, stderr = result.stdout or b"", result.stderr or b""
        fields = verify_run(case, backend, stdout, output_dir, root / "opt/bench/expected", options)
        row.update(fields, status="ok", seconds=elapsed)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        row.update(status="timeout" if isinstance(error, subprocess.TimeoutExpired) else "failed", error=str(error))
        if isinstance(error, subprocess.SubprocessError):
            stdout, stderr = error.stdout or b"", error.stderr or b""
    tag = f"{round_number:02}-{case}-{backend}"
    (options.output / f"{tag}.stdout").write_bytes(stdout)
    if stderr:
        (options.output / f"{tag}.stderr").write_bytes(stderr)
    print(f"{tag:38} {row['status']} {row.get('seconds', '')} {row.get('error', '')}", flush=True)
    return row


def summary_for(rows, options):
    stats = {}
    for case in options.case:
        case_rows = [row for row in rows if row["round"] > 0 and row["case"] == case]
        if case == "coremark" and len({row["final_crc"] for row in case_rows if row["status"] == "ok"}) > 1:
            raise ValueError("CoreMark final CRC differs across successful runs")
        report = stats[case] = {}
        for backend in options.backend:
            group = [row for row in case_rows if row["backend"] == backend]
            valid = [row for row in group if row["status"] == "ok"]
            report[backend] = {"samples": len(valid), "failed": len(group) - len(valid)}
            if len(valid) != options.repeats:
                continue
            metric = "score" if case == "coremark" or case.startswith("scimark") else (
                "guest_seconds" if case == "gemm" else "seconds"
            )
            values = [row[metric] for row in valid]
            unit = "iterations/s" if case == "coremark" else ("MFLOPS" if case.startswith("scimark") else "s")
            if case in ("sha256sum", "gzip"):
                mib = 64 if case == "sha256sum" else 16
                values = [mib / seconds for seconds in values]
                unit = "MiB/s"
            entry = report[backend]
            entry.update(values=values, median=statistics.median(values), unit=unit)
            if len(values) > 1:
                q1, _, q3 = statistics.quantiles(values, n=4, method="inclusive")
                entry["iqr"] = [q1, q3]
    return stats


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--state", type=Path, required=True, help="Directory for disposable guest filesystem copies")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--case", choices=CASES, action="append")
    parser.add_argument("--backend", choices=BACKENDS, action="append", required=True)
    parser.add_argument("--podish", type=Path, default=Path("/opt/podish/Podish.Cli.dll"))
    parser.add_argument("--ish", type=Path)
    parser.add_argument("--fakefsify", type=Path)
    parser.add_argument("--ish-commit")
    parser.add_argument("--qemu-user", default="qemu-i386")
    parser.add_argument("--qemu-system", default="qemu-system-i386")
    parser.add_argument("--qemu-cpu", default="max")
    parser.add_argument("--jit-mode", choices=("exact", "region"), default="exact")
    parser.add_argument("--jit-arena", default="400m")
    parser.add_argument("--block-cache", default="400m")
    parser.add_argument("--repeats", type=int, default=7)
    parser.add_argument("--warmup", type=int, default=1, help="Unscored rounds before measurements")
    parser.add_argument("--iterations", type=int)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--commit", default=os.environ.get("PODISH_BENCH_COMMIT"))
    parser.add_argument("--machine", required=True, help="Label identifying the actual host and environment")
    options = parser.parse_args()
    if not options.commit:
        parser.error("pass --commit when running outside the benchmark host image")
    built_commit = os.environ.get("PODISH_BENCH_COMMIT")
    if built_commit and built_commit != options.commit:
        parser.error(f"container was built from {built_commit}, requested {options.commit}")
    options.case = options.case or list(CASES)
    if options.iterations is None:
        options.iterations = 5000 if options.smoke else 500000
    if options.smoke:
        options.warmup = 0
    if options.warmup < 0:
        parser.error("warmup rounds cannot be negative")
    if min(options.iterations, options.repeats, options.timeout) <= 0:
        parser.error("iterations, repeats and timeout must be positive")
    if len(set(options.backend)) != len(options.backend) or len(set(options.case)) != len(options.case):
        parser.error("list each selected case and backend once")
    if "ish" in options.backend and not (options.ish and options.fakefsify and options.ish_commit):
        parser.error("iSH requires --ish, --fakefsify and --ish-commit")
    for name in ("bundle", "state", "output", "podish", "ish", "fakefsify"):
        path = getattr(options, name)
        if path is not None:
            setattr(options, name, path.resolve())
    if "podish-jit" in options.backend and not (sys.platform == "darwin" and platform.machine() == "arm64"):
        parser.error("Podish Host LLVM JIT requires native macOS arm64")

    fixture = verify_bundle(options.bundle)
    options.state.mkdir(parents=True, exist_ok=True)
    options.output.mkdir(parents=True, exist_ok=True)
    if (options.output / "runs.jsonl").exists():
        parser.error("choose a fresh output directory")
    root = options.state / "rootfs"
    archive = options.bundle / "rootfs.tar.gz"
    root_hash = fixture[archive.name]
    prepare_root(archive, root, root_hash)
    ish_root = options.state / "ish-root"
    if "ish" in options.backend:
        prepare_root(archive, ish_root, root_hash, options.fakefsify)
    if "qemu-system" in options.backend:
        prepare_initrd(options.bundle / "rootfs.cpio.gz", options.state / "rootfs.cpio", fixture["rootfs.cpio.gz"])
    meta = {
        "machine": options.machine, "platform": platform.platform(), "arch": platform.machine(),
        "fixture": fixture, "commit": options.commit, "iterations": options.iterations,
        "repeats": options.repeats, "warmup": options.warmup, "smoke": options.smoke,
        "affinity": sorted(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None,
        "guest_packages": (root / "opt/bench/expected/guest-packages").read_text().splitlines(),
        "guest_compiler": (root / "opt/bench/expected/guest-compiler").read_text().strip(),
        "guest_elfs": (root / "opt/bench/expected/guest-elf-sha256").read_text().splitlines(),
    }
    options.coremark_build = None
    coremark_metadata = root / "opt/bench/expected/coremark-build"
    if "coremark" in options.case and coremark_metadata.is_file():
        build = dict(line.split("=", 1) for line in coremark_metadata.read_text().splitlines())
        expected_elfs = {name: checksum for checksum, name in (line.split() for line in meta["guest_elfs"])}
        build["sha256"] = digest(root / "opt/bench/coremark")
        if build["sha256"] != expected_elfs["coremark"]:
            raise ValueError("CoreMark ELF does not match the fixture manifest")
        if "ish" in options.backend and digest(ish_root / "data/opt/bench/coremark") != build["sha256"]:
            raise ValueError("iSH import altered the CoreMark ELF")
        options.coremark_build = meta["coremark_build"] = build
    if "ish" in options.backend:
        meta["ish_commit"] = options.ish_commit
        meta["ish_sha256"] = digest(options.ish)
    if "qemu-user" in options.backend:
        meta["qemu_user"] = command_output([options.qemu_user, "--version"]).splitlines()[0]
    if "qemu-system" in options.backend:
        meta["qemu_system"] = command_output([options.qemu_system, "--version"]).splitlines()[0]
    if "qemu-user" in options.backend or "qemu-system" in options.backend:
        meta["qemu_cpu"] = options.qemu_cpu
    if any(backend.startswith("podish-") for backend in options.backend):
        library = options.podish.parent / ("libfibercpu.dylib" if sys.platform == "darwin" else "libfibercpu.so")
        native = ctypes.CDLL(str(library))
        native.X86_GetOptimizationConfig.restype = ctypes.c_char_p
        config = native.X86_GetOptimizationConfig().decode()
        if "static=ON;fusion=EXTENDED;enabled=ON;" not in config:
            raise ValueError(f"unexpected Podish optimization configuration: {config}")
        meta.update(podish_optimization=config, podish_library_sha256=digest(library),
                    block_cache=options.block_cache, jit_mode=options.jit_mode, jit_workers=2,
                    jit_arena=options.jit_arena)
    (options.output / "metadata.json").write_text(json.dumps(meta, indent=2) + "\n")

    rows = []
    with (options.output / "runs.jsonl").open("w", buffering=1) as log:
        for round_number in range(1 - options.warmup, options.repeats + 1):
            cases = options.case if round_number % 2 else options.case[::-1]
            for index, case in enumerate(cases):
                offset = (round_number + index) % len(options.backend)
                arms = options.backend[offset:] + options.backend[:offset]
                for backend in arms:
                    row = measure(options, root, ish_root, case, backend, round_number)
                    rows.append(row)
                    log.write(json.dumps(row) + "\n")
    (options.output / "summary.json").write_text(json.dumps(summary_for(rows, options), indent=2) + "\n")
    return 1 if any(row["status"] != "ok" for row in rows) else 0


if __name__ == "__main__":
    sys.exit(main())

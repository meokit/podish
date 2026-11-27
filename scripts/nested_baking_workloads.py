"""Run and verify the guest workload suites used in the baking study."""

from contextlib import contextmanager
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import re
import selectors
import shlex
import shutil
import signal
import subprocess
import tarfile
import time


def read_json(path):
    return json.loads(path.read_text())


def write_json(path, data):
    pending = path.with_suffix(".tmp")
    pending.write_text(json.dumps(data, indent=2) + "\n")
    pending.replace(path)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def canonical_output(data, mode):
    if mode == "bytes":
        return data
    if mode == "sorted-lines":
        return b"".join(sorted(data.splitlines(keepends=True)))
    if mode == "gzip":
        return gzip.decompress(data)
    if mode == "tar":
        with tarfile.open(fileobj=io.BytesIO(data)) as archive:
            rows = sorted((member.name, digest(archive.extractfile(member).read()))
                          for member in archive if member.isfile())
    elif mode == "du":
        sizes = (line.split("\t", 1) for line in data.decode().splitlines())
        rows = sorted((name, int(size)) for size, name in sizes if "/" in name)
    elif mode == "ls":
        directory = None
        rows = []
        for line in data.decode().splitlines():
            if line.endswith(":"):
                directory = line[:-1]
            elif line:
                rows.append(f"{directory}/{line}")
        rows.sort()
    else:
        raise ValueError(f"unsupported output check: {mode}")
    return json.dumps(rows).encode()


@contextmanager
def benchmark_lock(path):
    with Path(path).open("a") as lock:
        if os.name == "nt":
            import msvcrt

            lock.seek(0)
            while True:
                try:
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                except PermissionError:
                    time.sleep(0.1)
                else:
                    break
            try:
                yield
            finally:
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            return

        import fcntl

        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)


def stage_cli(managed, directory, library):
    shutil.copytree(managed, directory, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("libfibercpu*.dylib", ".fiberpod"))
    shutil.copy2(library, directory / "libfibercpu.dylib")
    return directory / "Podish.Cli.dll"


def run_workloads(cfg, cli, cases, log, capture=None, *, native_defaults=False, jit_mode=None):
    rootfs = Path(cfg["rootfs"])
    shell = ["set -eu"]
    files = []
    for i, case in enumerate(cases):
        filename = f".nested-bake-output-{i}"
        output = rootfs / case["cwd"].lstrip("/") / filename
        output.unlink(missing_ok=True)
        files.append(output)
        repetitions = case["repetitions"] if capture is None else 1
        command = f"({case['shell']}) > {filename}"
        shell.extend([f"cd {shlex.quote(case['cwd'])}", f"printf 'BAKE_BEGIN {i}\\n'"])
        if repetitions == 1:
            shell.append(command)
        else:
            shell.extend([
                "i=0",
                f'while [ "$i" -lt {repetitions} ]; do',
                f"  {command}",
                "  i=$((i+1))",
                "done",
            ])
        shell.append(f"printf 'BAKE_END {i}\\n'")
    argv = [cfg.get("dotnet", "dotnet"), str(cli), "run", "--rm",
            "--rootfs", str(rootfs), "--log-driver", "none", "--memory", "1G"]
    if jit_mode is not None or not native_defaults:
        argv.append(f"-XX:JitMode={jit_mode if jit_mode is not None else 'off'}")
    if not native_defaults:
        argv.append("-XX:BlockCacheSize=400m")
    argv.extend(["-e", "LC_ALL=C", "-e", "LANG=C", "-e", "TZ=UTC"])
    if capture is not None:
        capture.mkdir(parents=True, exist_ok=True)
        if cfg.get("guest") == "aarch64":
            argv.append(f"-XX:AArch64ProfilePath={capture / 'words.json'}")
        else:
            argv.extend(["--guest-stats-dir", str(capture)])
    argv.extend(["--", "/bin/sh", "-c", "\n".join(shell)])
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(("FIBERCPU_", "PODISH_", "DYLD_", "DOTNET_STARTUP_HOOKS"))}
    if not native_defaults:
        env.update(FIBERCPU_DYNAMIC_SPECIALIZATION="0", FIBERCPU_LINKED_BLOCK_CACHE="0")
        if jit_mode in (None, "off"):
            env["FIBERCPU_LLVM_JIT"] = "0"
    env["PODISH_SKIP_BLOCKS_EXPORT"] = "1" if capture is None else "0"
    marks = {}
    pending = b""
    deadline = time.monotonic() + cfg.get("workload_timeout", 120)
    with log.open("wb") as trace, subprocess.Popen(
            argv, cwd=cli.parent, env=env, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True) as process:
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(process.stdout, selectors.EVENT_READ)
                while selector.select(max(0, deadline - time.monotonic())):
                    chunk = os.read(process.stdout.fileno(), 65536)
                    now = time.monotonic()
                    if not chunk:
                        break
                    trace.write(chunk)
                    lines = (pending + chunk).split(b"\n")
                    pending = lines.pop()
                    for line in lines:
                        marker = re.fullmatch(rb"BAKE_(BEGIN|END) (\d+)\r?", line)
                        if marker:
                            marks[marker[1], int(marker[2])] = now
                else:
                    raise RuntimeError(f"workload timed out: {log}")
            if process.wait(timeout=max(0.1, deadline - time.monotonic())):
                raise RuntimeError(f"guest exited with status {process.returncode}: {log}")
        finally:
            if process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait()
    timings = {}
    for i, (case, output) in enumerate(zip(cases, files)):
        begin = marks.get((b"BEGIN", i))
        end = marks.get((b"END", i))
        if begin is None or end is None or end < begin or (end == begin and capture is None):
            raise RuntimeError(f"{case['id']}: missing or unresolved timing markers: {log}")
        data = canonical_output(output.read_bytes(), case.get("verify", "bytes"))
        observed = digest(data)
        if observed != case["sha256"]:
            raise RuntimeError(f"{case['id']}: output SHA-256 {observed}, expected {case['sha256']}: {log}")
        timings[case["id"]] = end - begin
        output.unlink()
    return timings


def split_families(cases, held_out):
    ids = [case["id"] for case in cases]
    families = {case.get("family", case["id"]) for case in cases}
    if len(ids) != len(set(ids)):
        raise ValueError("workload IDs must be unique")
    held_out = set(held_out)
    if held_out - families:
        raise ValueError(f"unknown holdout families: {sorted(held_out - families)}")
    held_out.update(case.get("family", case["id"]) for case in cases if case.get("holdout"))
    training = [c for c in cases if c.get("family", c["id"]) not in held_out]
    if not training:
        raise ValueError("at least one training family is required")
    return training, [c for c in cases if c.get("family", c["id"]) in held_out]

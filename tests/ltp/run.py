#!/usr/bin/env python3
"""Run the same committed LTP ELF32 binaries on Linux and in Podish.

Only the case manifest is executed; never discovers/runs arbitrary LTP tests.
No third-party Python packages, guest shell or guest test runner are required.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import tempfile
import time

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
SUMMARY_KEYS = ("passed", "failed", "broken", "skipped", "warnings")


def classify(returncode, text, timed_out=False):
    """LTP exit values are a bitmask, not a conventional boolean exit status."""
    counts = {key: 0 for key in SUMMARY_KEYS}
    summaries = re.findall(r"Summary:\s*\n((?:[a-z]+\s+\d+\s*\n){5})", text)
    has_summary = bool(summaries)
    if has_summary:
        for summary in summaries:
            for key in counts:
                match = re.search(rf"^{key}\s+(\d+)\s*$", summary, re.MULTILINE)
                if match:
                    counts[key] += int(match[1])
                else:
                    has_summary = False
    else:
        # Legacy LTP API (test.h) has no Summary block. Its result lines are
        # still authoritative; require at least one result to avoid treating
        # a silent/crashed binary as a pass.
        legacy = re.findall(r"^\S+\s+\d+\s+(TPASS|TFAIL|TBROK|TCONF|TWARN)\b", text, re.MULTILINE)
        legacy += re.findall(r"Child process returned (TPASS|TFAIL|TBROK|TCONF|TWARN)\b", text)
        legacy_map = {"TPASS": "passed", "TFAIL": "failed", "TBROK": "broken",
                      "TCONF": "skipped", "TWARN": "warnings"}
        for result in legacy:
            counts[legacy_map[result]] += 1
        has_summary = bool(legacy)
    if timed_out:
        return "TIMEOUT", counts
    if returncode < 0 or returncode > 63 or not has_summary:
        return "BROKEN", counts
    if counts["broken"] or returncode & 2 or re.search(r": TBROK:", text):
        return "BROKEN", counts
    if counts["failed"] or returncode & 1 or re.search(r": TFAIL:", text):
        return "FAIL", counts
    if counts["warnings"] or returncode & 4 or re.search(r": TWARN:", text):
        return "BROKEN", counts
    if returncode not in (0, 32):
        return "BROKEN", counts
    if counts["passed"] and returncode in (0, 32):
        return "PASS", counts
    if counts["skipped"] and not counts["passed"] and returncode == 32:
        return "SKIP", counts
    return "BROKEN", counts


GUEST_PASSWD = (
    "root:x:0:0:root:/:/bin/sh\n"
    "daemon:x:1:1:daemon:/:/bin/false\n"
    "bin:x:2:2:bin:/:/bin/false\n"
    "user1:x:1000:1000:user1:/:/bin/false\n"
    "user2:x:1001:1001:user2:/:/bin/false\n"
    "nobody:x:65534:65534:nobody:/:/bin/false\n")
GUEST_GROUP = (
    "root:x:0:\ndaemon:x:1:\nbin:x:2:\nusers:x:100:\n"
    "user1:x:1000:\nuser2:x:1001:\nnogroup:x:65534:\n")


def write_guest_identity_files(root):
    (root / "etc").mkdir(parents=True, exist_ok=True)
    (root / "etc/passwd").write_text(GUEST_PASSWD)
    (root / "etc/group").write_text(GUEST_GROUP)
    (root / "etc/nsswitch.conf").write_text("passwd: files\ngroup: files\n")


def build_image_tar(root, tar_path, binaries):
    """Create an OCI rootfs tar with explicit Unix metadata.

    Windows tar tools commonly leave executable files at 0644; the image
    loader correctly rejects that. Do not rely on the host filesystem mode.
    """
    with tarfile.open(tar_path, "w", format=tarfile.PAX_FORMAT) as archive:
        for dirpath, dirnames, filenames in os.walk(root):
            relative = os.path.relpath(dirpath, root)
            relative = "" if relative == "." else relative.replace(os.sep, "/")
            for directory in sorted(dirnames):
                name = f"{relative}/{directory}" if relative else directory
                info = archive.gettarinfo(str(Path(dirpath) / directory), name)
                info.mode = 0o1777 if name == "tmp" else 0o755
                info.uid = info.gid = 0
                archive.addfile(info)
            for filename in sorted(filenames):
                name = f"{relative}/{filename}" if relative else filename
                path = Path(dirpath) / filename
                info = archive.gettarinfo(str(path), name)
                info.mode = 0o755 if name in binaries else 0o644
                info.uid = info.gid = 0
                with path.open("rb") as source:
                    archive.addfile(info, source)


def execute(command, cwd, env, timeout):
    options = {"start_new_session": True} if os.name != "nt" else {}
    started = time.monotonic()
    with subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **options) as proc:
        timed_out = False
        try:
            output, _ = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                os.killpg(proc.pid, signal.SIGKILL)
            output, _ = proc.communicate()
    return proc.returncode, output.decode("utf-8", errors="replace"), timed_out, time.monotonic() - started


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", choices=("native", "podish"), required=True)
    parser.add_argument("--artifacts", type=Path, default=REPO / "artifacts/ltp")
    parser.add_argument("--manifest", type=Path, default=HERE / "cases.json")
    parser.add_argument("--cli", type=Path, default=REPO / "Podish.Cli/bin/Debug/net10.0/Podish.Cli.dll")
    parser.add_argument("--baseline", type=Path, help="Native report (required for Podish; defaults to results/native.json)")
    parser.add_argument("--output", type=Path, help="JSON report; defaults to results/<backend>.json")
    parser.add_argument("--filter", default="*", help="Comma-separated glob patterns for case names")
    parser.add_argument("--timeout", type=float, default=30, help="Host wall-clock seconds per binary")
    parser.add_argument("--fs-backend", choices=("hostfs", "tmpfs"), default=None,
                        help="Override the manifest's Podish root filesystem (hostfs or tmpfs image)")
    parser.add_argument("--trace", action="store_true", help="Enable Podish syscall tracing")
    parser.add_argument("--native-work-root", type=Path, default=Path("/tmp"),
                        help="Linux scratch filesystem, not WSL's /mnt/c (32-bit inode overflow/FIFO limitations)")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    if args.backend == "native" and platform.system() != "Linux":
        parser.error("Native baseline requires Linux (on Windows use WSL)")
    manifest = json.loads(args.manifest.read_text())
    if args.backend == "native":
        if args.fs_backend not in (None, "hostfs"):
            parser.error("--fs-backend applies only to the Podish backend")
        fs_backend = "hostfs"
    else:
        fs_backend = args.fs_backend or manifest.get("podishFsBackend", "hostfs")
    if fs_backend not in ("hostfs", "tmpfs"):
        parser.error("Manifest podishFsBackend must be hostfs or tmpfs")
    if args.backend == "native" and manifest.get("requiresRoot") and os.geteuid() != 0:
        parser.error("This manifest requires a root native baseline (use WSL as root)")
    out = args.artifacts.resolve()
    metadata = json.loads((out / "build.json").read_text())
    if metadata.get("manifest", "cases.json") != args.manifest.name:
        parser.error("Artifacts were built from a different case manifest")
    commit = subprocess.check_output(["git", "-C", str(HERE / "upstream"), "rev-parse", "HEAD"], text=True).strip()
    if metadata["ltpCommit"] != commit:
        parser.error("LTP submodule changed; rebuild the test binaries")
    cases = manifest["cases"]
    patterns = args.filter.split(",")
    cases = [case for case in cases if any(fnmatch.fnmatchcase(case["name"], pattern) for pattern in patterns)]
    if not cases:
        parser.error("No cases matched --filter")
    baseline = {}
    if args.backend == "podish":
        args.cli = args.cli.resolve()
        if not args.cli.is_file():
            parser.error("Build Podish.Cli first, or supply --cli")
        baseline_path = args.baseline or out / "results/native.json"
        baseline_report = json.loads(baseline_path.read_text())
        if baseline_report["backend"] != "native" or baseline_report["build"] != metadata:
            parser.error("Baseline must use exactly the same build.json and ELF binaries")
        baseline = {case["name"]: case for case in baseline_report["cases"]}
    for case in cases:
        name = case["name"]
        binary = out / "bin" / name
        if hashlib.sha256(binary.read_bytes()).hexdigest() != metadata["binaries"][name]:
            parser.error(f"Binary hash mismatch: {name}; rebuild")
        if args.backend == "podish" and (name not in baseline or baseline[name]["status"] != "PASS"):
            parser.error(f"No passing native baseline for {name}")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    run_dir = out / "runs" / f"{stamp}-{args.backend}-{fs_backend}"
    run_dir.mkdir(parents=True)
    # Skip kernel-config inspection, not feature/syscall assertions: Podish
    # has no host kernel .config and the selected eventfd cases probe behavior.
    common_env = {"LC_ALL": "C", "LTP_COLORIZE_OUTPUT": "0", "LTP_TIMEOUT_MUL": "2",
                  "KCONFIG_SKIP_CHECK": "1"}
    base_env = os.environ.copy()
    base_env.update(common_env)
    # Preserve the repository's net10.0 TFM when using a net11-only host.
    base_env.setdefault("DOTNET_ROLL_FORWARD", "LatestMajor")
    image_ref = "localhost/podish-ltp:latest"
    image_work = None
    if args.backend == "podish" and fs_backend == "tmpfs":
        image_work = run_dir / "image-store"
        image_work.mkdir()
        image_root = run_dir / "image-root"
        for directory in ("opt/ltp/testcases/bin", "tmp", "dev", "proc", "etc", "run"):
            (image_root / directory).mkdir(parents=True, exist_ok=True)
        write_guest_identity_files(image_root)
        binary_names = set()
        for case in cases:
            relative = f"opt/ltp/testcases/bin/{case['name']}"
            shutil.copy2(out / "bin" / case["name"], image_root / relative)
            binary_names.add(relative)
        tar_path = run_dir / "image.tar"
        build_image_tar(image_root, tar_path, binary_names)
        import_code, import_text, import_timed_out, _ = execute(
            ["dotnet", str(args.cli), "import", str(tar_path), image_ref],
            image_work, base_env, 120)
        (run_dir / "import.log").write_text(import_text, encoding="utf-8")
        if import_code != 0 or import_timed_out:
            print(f"Image import failed (rc={import_code}):\n{import_text}", file=sys.stderr)
            return 1
    results = []
    for case in cases:
        name = case["name"]
        work = run_dir / name
        work.mkdir()
        env = base_env.copy()
        native_work = None
        cwd = work
        if args.backend == "native":
            native_work = tempfile.TemporaryDirectory(prefix=f"podish-ltp-{name}-", dir=args.native_work_root)
            cwd = Path(native_work.name)
            tmp = cwd / "tmp"
            tmp.mkdir()
            env.update(TMPDIR=str(tmp), LTPROOT=str(out))
            command = [str(out / "bin" / name)]
        else:
            # --init keeps LTP's harness parent above PID 1; its heartbeat checks
            # getppid()==1 as an orphan condition. No shell is needed.
            command = ["dotnet", str(args.cli)]
            if args.trace:
                command.extend(["--log-level", "trace", "--log-file", str(work / "engine.log")])
            command.extend(["run", "--rm", "--init", "--log-driver", "none",
                            "--network", "private"])
            if image_work is not None:
                cwd = image_work
                command.extend(["--fs-backend", "tmpfs", image_ref])
            else:
                root = work / "rootfs"
                for directory in ("opt/ltp/testcases/bin", "tmp", "dev", "proc", "etc", "run"):
                    (root / directory).mkdir(parents=True, exist_ok=True)
                shutil.copy2(out / "bin" / name, root / "opt/ltp/testcases/bin" / name)
                write_guest_identity_files(root)
                command.extend(["--rootfs", str(root)])
            if args.trace:
                command.append("--strace")
            for key, value in {**common_env, "TMPDIR": "/tmp", "LTPROOT": "/opt/ltp"}.items():
                command.extend(["--env", f"{key}={value}"])
            command.append(f"/opt/ltp/testcases/bin/{name}")
        try:
            code, text, timed_out, elapsed = execute(command, cwd, env, args.timeout)
            status, counts = classify(code, text, timed_out)
            if status == "PASS" and args.backend == "podish" and counts != baseline[name]["counts"]:
                native_counts = baseline[name]["counts"]
                extra_skips_only = (
                    counts["passed"] > 0 and
                    counts["failed"] == native_counts["failed"] == 0 and
                    counts["broken"] == native_counts["broken"] == 0 and
                    counts["warnings"] == native_counts["warnings"] == 0 and
                    counts["skipped"] > native_counts["skipped"])
                status = "PARTIAL" if extra_skips_only else "BROKEN"
                text += ("\nRunner: partial variant coverage (unsupported assertions skipped); "
                         "refusing a full PASS.\n" if status == "PARTIAL" else
                         "\nRunner: assertion counts differ from native; refusing a partial-coverage PASS.\n")
        except OSError as error:
            code, text, elapsed = -1, str(error), 0
            status, counts = "BROKEN", {key: 0 for key in SUMMARY_KEYS}
        finally:
            if native_work is not None:
                native_work.cleanup()
        log = work / "output.log"
        log.write_text(text, encoding="utf-8")
        result = {"name": name, "group": case["group"], "status": status, "returncode": code,
                  "seconds": round(elapsed, 3), "counts": counts, "log": str(log),
                  "nativeStatus": baseline.get(name, {}).get("status")}
        results.append(result)
        print(f"{name:24} {status:8} {elapsed:6.2f}s  {counts}", flush=True)
    statuses = dict(Counter(case["status"] for case in results))
    report = {"backend": args.backend, "fileSystemBackend": fs_backend,
              "build": metadata, "host": platform.platform(),
              "manifest": args.manifest.name, "requiresRoot": bool(manifest.get("requiresRoot")),
              "timestamp": stamp, "kernelConfigChecks": "disabled (behavioral assertions retained)",
              "timeoutSeconds": args.timeout, "summary": statuses, "cases": results}
    output = (args.output or out / "results" / f"{args.backend}.json").resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    (run_dir / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Summary: {statuses}\nReport: {output}")
    return int(any(case["status"] != "PASS" for case in results))


if __name__ == "__main__":
    sys.exit(main())

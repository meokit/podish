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
FS_BACKENDS = ("hostfs", "tmpfs", "silkfs")
IMAGE_BACKENDS = ("tmpfs", "silkfs")


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


def source_state():
    """Identify local code as well as committed code; artifacts are git-ignored."""
    revision = subprocess.check_output(["git", "-C", str(REPO), "rev-parse", "HEAD"],
                                       encoding="utf-8", errors="replace").strip()
    # A --binary diff carries arbitrary bytes; decode as UTF-8 so the host locale
    # (for example GBK on Windows) cannot make this report step fail.
    diff = subprocess.check_output(["git", "-C", str(REPO), "diff", "--binary", "HEAD"],
                                   encoding="utf-8", errors="replace")
    untracked = subprocess.check_output(
        ["git", "-C", str(REPO), "ls-files", "--others", "--exclude-standard"],
        encoding="utf-8", errors="replace").splitlines()
    digest = hashlib.sha256(diff.encode("utf-8"))
    for name in sorted(untracked):
        path = REPO / name
        if path.is_file():
            digest.update(name.encode("utf-8"))
            digest.update(path.read_bytes())
    return {"revision": revision, "dirty": bool(diff or untracked), "changesSha256": digest.hexdigest()}


def podish_command(cli, fs_backend, work, binaries, env, executable, image_ref=None, trace=False):
    """Use identical backend/container routing for the preflight and LTP cases."""
    command = ["dotnet", str(cli)]
    if trace:
        command.extend(["--log-level", "trace", "--log-file", str(work / "engine.log")])
    command.extend(["run", "--rm", "--init", "--log-driver", "none", "--network", "private"])
    if fs_backend in IMAGE_BACKENDS:
        if image_ref is None:
            raise ValueError("Image backend requires an imported image")
        command.extend(["--fs-backend", fs_backend, image_ref])
    else:
        root = work / "rootfs"
        for directory in ("opt/ltp/testcases/bin", "tmp", "dev", "proc", "etc", "run"):
            (root / directory).mkdir(parents=True, exist_ok=True)
        for binary in binaries:
            shutil.copy2(binary, root / "opt/ltp/testcases/bin" / binary.name)
        write_guest_identity_files(root)
        command.extend(["--rootfs", str(root)])
    if trace:
        command.append("--strace")
    for key, value in env.items():
        command.extend(["--env", f"{key}={value}"])
    command.append(executable)
    return command


def verify_backend_probe(text, fs_backend):
    """Mount evidence is not proof of the host pointer/zero-copy data path."""
    observations = [json.loads(line) for line in text.splitlines() if line.startswith('{"schema":')]
    if len(observations) != 1 or observations[0].get("schema") != 1:
        raise ValueError("Missing or malformed backend probe observation")
    observation = observations[0]
    if observation.get("sharedMappingCoherent") is not True:
        raise ValueError("Backend probe shared mapping was not coherent")
    mounts = []
    path_warnings = []
    for line in observation["mountinfo"].splitlines():
        before, after = line.split(" - ", 1)
        # Linux mountinfo escapes whitespace/backslashes as octal.
        point = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m[1], 8)), before.split()[4])
        normalized = re.sub(r"/+", "/", point)
        if normalized != point:
            path_warnings.append({"reported": point, "normalized": normalized})
        point = normalized
        mounts.append((point, after.split()[0]))

    def mount_for(path):
        matches = [(point, kind) for point, kind in mounts
                   if point == "/" or path == point or path.startswith(point.rstrip("/") + "/")]
        if not matches:
            raise ValueError(f"No mount for probe path {path}")
        point, kind = max(matches, key=lambda entry: len(entry[0]))
        return {"mountPoint": point, "fsType": kind}

    routes = {path: mount_for(path) for path in ("/", "/tmp", "/dev/shm", observation["dataPath"])}
    for path in ("/", "/tmp", observation["dataPath"]):
        if routes[path]["fsType"] != fs_backend:
            raise ValueError(f"Probe {path} routed to {routes[path]['fsType']}, requested {fs_backend}")
    if routes["/dev/shm"]["fsType"] != "tmpfs" or observation.get("shmFsMagic") != 0x01021994:
        raise ValueError("Expected the independent /dev/shm tmpfs mount")
    return {"status": "mounts-verified", "scope": "separate-preflight-container",
            "directMappingVerified": False, "routes": routes, "pathWarnings": path_warnings,
            "observation": observation}


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
    parser.add_argument("--fs-backend", choices=FS_BACKENDS, default=None,
                        help="Override the manifest's Podish filesystem (hostfs, tmpfs or silkfs)")
    parser.add_argument("--fs-probe", type=Path,
                        help="Optional static-i386 backend probe; mount failures block the run")
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
    if fs_backend not in FS_BACKENDS:
        parser.error("Manifest podishFsBackend must be hostfs, tmpfs or silkfs")
    if args.fs_probe is not None:
        args.fs_probe = args.fs_probe.resolve()
        if args.backend != "podish" or not args.fs_probe.is_file():
            parser.error("--fs-probe requires Podish and an existing probe binary")
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
    if args.backend == "podish" and fs_backend in IMAGE_BACKENDS:
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
        if args.fs_probe is not None:
            relative = f"opt/ltp/testcases/bin/{args.fs_probe.name}"
            if args.fs_probe.name in {case["name"] for case in cases}:
                parser.error("Probe filename must not replace an LTP binary")
            shutil.copy2(args.fs_probe, image_root / relative)
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
    provenance = {"status": "not-collected", "directMappingVerified": False}
    if args.fs_probe is not None:
        probe_work = run_dir / "backend-probe"
        probe_work.mkdir()
        command = podish_command(args.cli, fs_backend, probe_work, [args.fs_probe],
                                 {**common_env, "TMPDIR": "/tmp", "LTPROOT": "/opt/ltp"},
                                 f"/opt/ltp/testcases/bin/{args.fs_probe.name}", image_ref)
        code, text, expired, _ = execute(command, image_work or probe_work, base_env, args.timeout)
        (probe_work / "output.log").write_text(text, encoding="utf-8")
        try:
            if expired or code != 0:
                raise ValueError(f"Backend probe failed: rc={code}, timeout={expired}")
            provenance = verify_backend_probe(text, fs_backend)
            provenance["binarySha256"] = hashlib.sha256(args.fs_probe.read_bytes()).hexdigest()
            (probe_work / "evidence.json").write_text(json.dumps(provenance, indent=2) + "\n")
        except (ValueError, KeyError, IndexError) as error:
            print(f"Backend preflight failed: {error}; see {probe_work / 'output.log'}", file=sys.stderr)
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
            cwd = image_work or work
            command = podish_command(args.cli, fs_backend, work, [out / "bin" / name],
                                     {**common_env, "TMPDIR": "/tmp", "LTPROOT": "/opt/ltp"},
                                     f"/opt/ltp/testcases/bin/{name}", image_ref, args.trace)
        try:
            code, text, timed_out, elapsed = execute(command, cwd, env, args.timeout)
            status, counts = classify(code, text, timed_out)
            if status == "PASS" and args.backend == "podish" and counts != baseline[name]["counts"]:
                native_counts = baseline[name]["counts"]
                more_passed_from_skips = (
                    counts["failed"] == native_counts["failed"] == 0 and
                    counts["broken"] == native_counts["broken"] == 0 and
                    counts["warnings"] == native_counts["warnings"] == 0 and
                    counts["passed"] >= native_counts["passed"] and
                    counts["passed"] + counts["skipped"] == native_counts["passed"] + native_counts["skipped"])
                if not more_passed_from_skips:
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
              "source": source_state(), "dataBackendEvidence": provenance,
              "build": metadata, "host": platform.platform(),
              "manifest": args.manifest.name, "requiresRoot": bool(manifest.get("requiresRoot")),
              "timestamp": stamp, "kernelConfigChecks": "disabled (behavioral assertions retained)",
              "timeoutSeconds": args.timeout, "summary": statuses, "cases": results}
    report_name = "native.json" if args.backend == "native" else f"podish-{fs_backend}.json"
    output = (args.output or out / "results" / report_name).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    (run_dir / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"Summary: {statuses}\nReport: {output}")
    return int(any(case["status"] != "PASS" for case in results))


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Capture a short Node.js block profile with the current native build."""

import argparse
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import time

from collect_baked_fusions import bake
from collect_modrm_hotness import DEFAULT_COVERAGE, collect_modrm_hotness, collect_workload_union, write_hotness
from collect_operand_specializations import ROOT, bake_scalar_workload_union


def capture_suite(run, rootfs, workspace):
    sys.path.insert(0, str(ROOT / "benchmark/optimization"))
    from run_matrix import COMMANDS, prepare_fixture, validate_gnu_output

    work = workspace / "gnu-fixture"
    expected = prepare_fixture(work)
    if not (rootfs / "work/manual").is_dir():
        raise ValueError("the suite rootfs needs a /work/manual directory for the GNU fixture mount")
    profiles = {}
    for name, command in COMMANDS.items():
        profile, _ = run(rootfs, f"gnu-{name}", ["/bin/sh", "-c", f"cd /work/manual && {command}"],
                         options=["--volume", f"{work}:/work/manual"])
        try:
            validate_gnu_output(work, name, expected)
        except AssertionError as error:
            raise ValueError(f"{name}: GNU output validation failed") from error
        profiles[f"gnu-{name}"] = profile
    for name, guest in (("coremark", "/work/coremark_zig"), ("coremark-gcc", "/work/coremark_gcc")):
        profile, stdout = run(rootfs, name, [guest, "0x0", "0x0", "0x66", "60000", "7", "1", "2000"])
        if "Correct operation validated" not in stdout:
            raise ValueError(f"{name}: CoreMark CRC validation failed")
        profiles[name] = profile
    return profiles


def train(args):
    podish = args.podish.resolve(strict=True)
    if podish.suffix.lower() != ".dll" or not podish.with_suffix(".runtimeconfig.json").is_file():
        raise ValueError("--podish must point to a framework-dependent Podish.Cli.dll")
    workload = (ROOT / "benchmark/podish_perf/modrm-training.cjs").read_text(encoding="utf-8")
    output = args.output.resolve()
    if podish.parent == output or podish.parent in output.parents:
        raise ValueError("publish Podish.Cli to a dedicated directory outside the training output tree")
    if args.bake_fusions and (not args.suite_rootfs or args.fusions):
        raise ValueError("--bake-fusions requires the unfused training suite")
    output.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, FIBERCPU_LLVM_JIT="0", FIBERCPU_DYNAMIC_SPECIALIZATION="0",
               FIBERCPU_LINKED_BLOCK_CACHE="0", PODISH_SKIP_BLOCKS_EXPORT="0")
    started = time.perf_counter()

    with tempfile.TemporaryDirectory(prefix="capture-", dir=output) as tmp:
        workspace = Path(tmp)
        cli = workspace / "cli"
        # Install the build's native library into a private copy of the managed CLI.
        shutil.copytree(podish.parent, cli, symlinks=True, ignore=shutil.ignore_patterns(args.library.name))
        shutil.copyfile(args.library, cli / args.library.name)

        def run(rootfs, name, command, options=()):
            stats = workspace / "profiles" / name
            print(f"Capturing {name}", flush=True)
            result = subprocess.run([
                str(args.dotnet), str(cli / podish.name), "run", "--rm",
                "--rootfs", str(rootfs.resolve(strict=True)), "--guest-stats-dir", str(stats),
                "--log-driver", "none", "--env", "LC_ALL=C", "--env", "TZ=UTC", *options, *command,
            ], env=env, stdout=subprocess.PIPE, text=True)
            print(result.stdout, end="", flush=True)
            result.check_returncode()
            return stats / "blocks.bin", result.stdout

        profile, _ = run(args.rootfs, "fast-deep-equal", [
            "/bin/sh", "-c", 'cd /work/webpack && exec /opt/node14 --random-seed=20261119 -e "$1"',
            "modrm-training", workload,
        ])
        if args.suite_rootfs:
            profiles = {"fast-deep-equal": profile, **capture_suite(run, args.suite_rootfs.resolve(strict=True), workspace)}
            report = collect_workload_union(profiles, args.fusions, DEFAULT_COVERAGE, scalar_policies=True)
            if args.bake_fusions:
                candidates = workspace / "baked-fusions-v3.json"
                operands = workspace / "baked-fusion-operands-v3.json"
                scalars = workspace / "baked-scalar-operands-v3.json"
                bake(list(profiles.values()), candidates, operands)
                bake_scalar_workload_union(profiles, scalars)
                for result in (candidates, operands, scalars):
                    result.replace(output / result.name)
            for name, capture in profiles.items():
                destination = output / name
                destination.mkdir(exist_ok=True)
                capture.replace(destination / capture.name)
        else:
            report = collect_modrm_hotness([profile], args.fusions, DEFAULT_COVERAGE, scalar_policies=True)
            profile.replace(output / profile.name)
        hotness = workspace / "modrm-hotness.json"
        write_hotness(hotness, report)
        hotness.replace(output / hotness.name)

    print(f"ModRM training completed in {time.perf_counter() - started:.2f}s. Outputs in {output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("dotnet", "podish", "library", "rootfs", "output"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--fusions", type=Path)
    parser.add_argument("--suite-rootfs", type=Path, help="Also capture GNU tools and both CoreMark builds")
    parser.add_argument("--bake-fusions", action="store_true", help="Bake scalar operands and fusion candidates from unfused captures")
    try:
        train(parser.parse_args())
    except subprocess.CalledProcessError as error:
        parser.exit(1, f"Podish training failed with exit status {error.returncode}\n")
    except (OSError, ValueError, struct.error) as error:
        parser.exit(1, f"ModRM training failed: {error}\n")


if __name__ == "__main__":
    main()

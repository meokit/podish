#!/usr/bin/env python3
"""Compare both Linux guests with the same Podish build, CoreMark source and compiler."""

import argparse
from pathlib import Path
import re
import subprocess
from statistics import median
import tempfile


def build_guest(source, rootfs, guest, zig):
    files = sorted(source.glob("core_*.c"))
    subprocess.run([zig, "cc", "-target", f"{guest}-linux-musl", "-O2", "-static",
                    "-DITERATIONS=60000", "-DPERFORMANCE_RUN=1", '-DFLAGS_STR="zig cc -O2"',
                    "-I.", "-Iposix", *files, source / "posix/core_portme.c",
                    "-o", rootfs / f"coremark-{guest}"], cwd=source, check=True)


def run(cli, rootfs, guest, iterations, dotnet):
    output = subprocess.check_output([dotnet, cli, "run", "--rm", "--log-level", "error",
                                     "--rootfs", rootfs, f"/coremark-{guest}",
                                     "0", "0", "0x66", str(iterations)], cwd=rootfs.parent, text=True)
    if "Correct operation validated" not in output:
        raise RuntimeError(f"{guest} failed CoreMark validation:\n{output}")
    crc = re.findall(r"\[0\]crc(?:list|matrix|state|final)\s*:\s*(0x\w+)", output)
    if len(crc) != 4:
        raise RuntimeError(f"missing {guest} CRCs:\n{output}")
    rate = float(re.search(r"Iterations/Sec\s*:\s*([.\d]+)", output)[1])
    print(f"{guest:8} {rate:.1f} iterations/s  {' '.join(crc)}", flush=True)
    return rate, crc


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", type=Path, required=True, help="Release Podish.Cli.dll with both guests")
    parser.add_argument("--coremark", type=Path, required=True, help="EEMBC CoreMark source checkout")
    parser.add_argument("--iterations", type=int, default=60000)
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--zig", default="zig")
    args = parser.parse_args()
    if min(args.iterations, args.repetitions) < 1:
        parser.error("iterations and repetitions must be positive")
    cli, source = args.cli.resolve(), args.coremark.resolve()
    guests = ["x86", "aarch64"]
    samples = {guest: [] for guest in guests}
    expected = None
    with tempfile.TemporaryDirectory(prefix="podish-coremark-") as directory:
        rootfs = Path(directory) / "rootfs"
        rootfs.mkdir()
        for guest in guests:
            build_guest(source, rootfs, guest, args.zig)
        for _ in range(args.repetitions):
            for guest in guests:
                rate, crc = run(cli, rootfs, guest, args.iterations, args.dotnet)
                if expected is not None and crc != expected:
                    raise RuntimeError(f"{guest} CRCs differ: {crc} vs {expected}")
                expected = crc
                samples[guest].append(rate)
            guests.reverse()
    rates = {guest: median(values) for guest, values in samples.items()}
    print(f"Median i386 {rates['x86']:.1f}, AArch64 {rates['aarch64']:.1f} iterations/s "
          f"({rates['aarch64'] / rates['x86']:.1%} of i386)")


if __name__ == "__main__":
    main()

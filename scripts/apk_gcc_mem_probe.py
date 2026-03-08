#!/usr/bin/env python3
import argparse
import re
import sys
import time
from pathlib import Path

import pexpect

MEM_KEYS = [
    "MemTotal",
    "MemFree",
    "MemAvailable",
    "Cached",
    "AnonPages",
    "Mapped",
    "Slab",
    "SReclaimable",
    "Committed_AS",
]


def parse_meminfo(raw: str) -> dict[str, int]:
    result: dict[str, int] = {}
    for line in raw.splitlines():
        m = re.match(r"^(\w+):\s+(\d+)\s+kB$", line.strip())
        if m:
            result[m.group(1)] = int(m.group(2))
    return result


def section(text: str, begin: str, end: str) -> str:
    m = re.search(re.escape(begin) + r"(.*?)" + re.escape(end), text, re.S)
    if not m:
        return ""
    return m.group(1).strip()


def fmt_kib(kib: int | None) -> str:
    if kib is None:
        return "n/a"
    gib = kib / (1024 * 1024)
    mib = kib / 1024
    if gib >= 1:
        return f"{kib} KiB ({gib:.2f} GiB)"
    return f"{kib} KiB ({mib:.1f} MiB)"


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe memory before/after 'apk add gcc' in Podish container")
    parser.add_argument(
        "--image",
        default="/Users/jiangyiheng/repos/x86emu/.fiberpod/oci/images/docker.io_i386_alpine_latest",
        help="Image/rootfs argument passed to podish run",
    )
    parser.add_argument("--timeout", type=int, default=2400, help="pexpect timeout in seconds")
    parser.add_argument("--log-file", default="/tmp/apk_gcc_mem_probe.log", help="Transcript log path")
    parser.add_argument(
        "--project-root",
        default=str(Path(__file__).resolve().parents[1]),
        help="Repository root",
    )
    args = parser.parse_args()

    script = r'''set -e
free -h
echo __MEMINFO_BEFORE_BEGIN__
cat /proc/meminfo
echo __MEMINFO_BEFORE_END__
START=$(date +%s)
apk add --no-cache gcc
RC=$?
END=$(date +%s)
echo __APK_RC__:$RC __APK_SEC__:$((END-START))
free -h
echo __MEMINFO_AFTER_BEGIN__
cat /proc/meminfo
echo __MEMINFO_AFTER_END__
'''

    cmd = [
        "run",
        "--project",
        "Podish.Cli/Podish.Cli.csproj",
        "--no-build",
        "--",
        "run",
        "--rm",
        args.image,
        "--",
        "/bin/sh",
        "-lc",
        script,
    ]

    print(f"[probe] project_root={args.project_root}")
    print(f"[probe] image={args.image}")
    print(f"[probe] transcript={args.log_file}")

    t0 = time.time()
    child = pexpect.spawn(
        "dotnet",
        cmd,
        cwd=args.project_root,
        encoding="utf-8",
        timeout=args.timeout,
    )

    with open(args.log_file, "w", encoding="utf-8") as logf:
        child.logfile_read = logf
        try:
            child.expect(pexpect.EOF)
        except pexpect.exceptions.TIMEOUT:
            child.close(force=True)
            print(f"[probe] TIMEOUT after {time.time() - t0:.1f}s, see {args.log_file}")
            return 2

    out = child.before or ""
    wall = time.time() - t0
    exitstatus = child.exitstatus

    mem_before_raw = section(out, "__MEMINFO_BEFORE_BEGIN__", "__MEMINFO_BEFORE_END__")
    mem_after_raw = section(out, "__MEMINFO_AFTER_BEGIN__", "__MEMINFO_AFTER_END__")
    mem_before = parse_meminfo(mem_before_raw)
    mem_after = parse_meminfo(mem_after_raw)

    apk = re.search(r"__APK_RC__:(\d+)\s+__APK_SEC__:(\d+)", out)
    apk_rc = int(apk.group(1)) if apk else -1
    apk_sec = int(apk.group(2)) if apk else -1

    free_chunks = re.findall(r"(?ms)^\s*total\s+used\s+free.*?(?:\n\S.*\n\S.*)", out)
    free_before = free_chunks[0].strip() if len(free_chunks) >= 1 else "(not found)"
    free_after = free_chunks[1].strip() if len(free_chunks) >= 2 else "(not found)"

    print("\n===== free -h BEFORE =====")
    print(free_before)
    print("\n===== free -h AFTER =====")
    print(free_after)

    print("\n===== apk result =====")
    print(f"podish_exit={exitstatus} apk_rc={apk_rc} apk_elapsed_reported={apk_sec}s wall_clock={wall:.1f}s")

    print("\n===== meminfo delta =====")
    for k in MEM_KEYS:
        b = mem_before.get(k)
        a = mem_after.get(k)
        if b is None or a is None:
            print(f"{k:>12}: before={fmt_kib(b)} after={fmt_kib(a)} delta=n/a")
            continue
        d = a - b
        sign = "+" if d >= 0 else ""
        print(f"{k:>12}: before={fmt_kib(b)} after={fmt_kib(a)} delta={sign}{fmt_kib(d)}")

    print(f"\n[probe] transcript saved to {args.log_file}")

    if exitstatus != 0 or apk_rc != 0:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

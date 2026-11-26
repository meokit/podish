"""Report or check the native text size of a linked shared library."""

import argparse
import os
from pathlib import Path
import re
import struct
import subprocess


def size_output(tool, options, library):
    return subprocess.check_output(
        [tool, *options, library], text=True, env={**os.environ, "LC_ALL": "C"}
    )


def pe_text_size(library):
    with library.open("rb") as source:
        dos = source.read(64)
        if dos[:2] != b"MZ":
            raise ValueError("missing DOS header")
        offset, = struct.unpack_from("<I", dos, 60)
        source.seek(offset)
        signature, _, count, _, _, _, optional_size, _ = struct.unpack(
            "<4sHHIIIHH", source.read(24)
        )
        if signature != b"PE\x00\x00":
            raise ValueError("missing PE signature")
        source.seek(optional_size, os.SEEK_CUR)
        sections = []
        for _ in range(count):
            name, virtual_size = struct.unpack_from("<8sI", source.read(40))
            if name.rstrip(b"\x00") == b".text":
                sections.append(virtual_size)
        return sections


def native_text_size(library, macho_size, elf_size):
    if macho_size:
        output = size_output(macho_size, ["-m"], library)
        sections = re.findall(r"^\s*Section __text:\s+(\d+)", output, re.MULTILINE)
    elif elf_size:
        output = size_output(elf_size, ["-A", "-d"], library)
        sections = re.findall(r"^\.text[ \t]+(\d+)", output, re.MULTILINE)
    else:
        sections = pe_text_size(library)
    if not sections:
        raise ValueError("missing text section")
    # Report the largest slice in a universal Mach-O.
    return max(map(int, sections))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    formats = parser.add_mutually_exclusive_group(required=True)
    formats.add_argument("--macho-size", metavar="TOOL")
    formats.add_argument("--elf-size", metavar="TOOL")
    formats.add_argument("--pe", action="store_true")
    size = parser.add_mutually_exclusive_group(required=True)
    size.add_argument("--hint", type=int, help="Reference size in bytes for a build report")
    size.add_argument("--limit", type=int, help="Maximum size in bytes for a calibration check")
    size.add_argument("--growth-hint", type=int, help="Reference number of additional bytes from baking")
    size.add_argument("--growth-limit", type=int, help="Maximum growth in bytes relative to --baseline")
    parser.add_argument("--baseline", type=Path, help="Matched unbaked library for --growth-limit")
    parser.add_argument("library", type=Path)
    args = parser.parse_args()
    if (args.growth_limit is None) != (args.baseline is None):
        parser.error("--growth-limit and --baseline must be used together")

    try:
        text_size = native_text_size(args.library, args.macho_size, args.elf_size)
        if args.baseline is not None:
            baseline_size = native_text_size(args.baseline, args.macho_size, args.elf_size)
    except (OSError, ValueError, struct.error, subprocess.CalledProcessError) as error:
        message = f"{args.library}: unable to measure native text: {error}"
        if args.hint is not None or args.growth_hint is not None:
            print(message)
            return
        parser.exit(1, message + "\n")

    if args.hint is not None:
        print(f"{args.library.name}: native text {text_size} bytes (macOS ARM64 hint {args.hint} bytes)")
        return
    if args.growth_hint is not None:
        print(f"{args.library.name}: native text {text_size} bytes (baking growth budget {args.growth_hint} bytes)")
        return
    if args.growth_limit is not None:
        growth = text_size - baseline_size
        if growth > args.growth_limit:
            parser.exit(1, f"{args.library}: native text grew by {growth} bytes, over the {args.growth_limit}-byte growth limit\n")
        print(f"{args.library.name}: native text {text_size} bytes, grew by {growth} / {args.growth_limit} bytes")
        return
    if text_size > args.limit:
        parser.exit(1, f"{args.library}: native text is {text_size} bytes, over the {args.limit}-byte limit\n")
    print(f"{args.library.name}: native text {text_size} / {args.limit} bytes")


if __name__ == "__main__":
    main()

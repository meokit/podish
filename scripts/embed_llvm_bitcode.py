#!/usr/bin/env python3
"""Embed one Clang bitcode module for the block JIT."""

import sys
from pathlib import Path

source, output = map(Path, sys.argv[1:])
data = source.read_bytes()
lines = ["constexpr uint8_t kBitcode[] = {"]
for start in range(0, len(data), 16):
    lines.append("    " + ", ".join(f"0x{byte:02x}" for byte in data[start:start + 16]) + ",")
lines.append("};")
output.write_text("\n".join(lines) + "\n")

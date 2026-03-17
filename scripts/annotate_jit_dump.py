#!/usr/bin/env python3
import argparse
import os
import re
import subprocess
import sys
import tempfile
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class OpInfo:
    index: int
    name: str
    code_size: int
    patches: int
    branch_relocs: int
    start_off: int = 0
    end_off: int = 0


@dataclass
class RelocInfo:
    op_index: int
    name: str
    branch_addr: int
    target_addr: int | None = None
    veneer_addr: int | None = None
    is_call: bool | None = None
    block_target_op: int | None = None
    runtime_op: int | None = None
    handler_addr: int | None = None
    kind: str = "direct"


@dataclass
class BlockInfo:
    start_eip: int
    code_addr: int | None = None
    code_size: int | None = None
    branch_relocs: int | None = None
    veneer_count: int | None = None
    veneer_bytes: int | None = None
    dumped_path: str | None = None
    ops: list[OpInfo] = field(default_factory=list)
    relocs: list[RelocInfo] = field(default_factory=list)


def run(cmd: list[str], check: bool = True) -> str:
    proc = subprocess.run(cmd, text=True, capture_output=True)
    if check and proc.returncode != 0:
        raise RuntimeError(f"command failed ({proc.returncode}): {' '.join(cmd)}\n{proc.stderr}")
    return proc.stdout


def parse_int(text: str) -> int:
    return int(text, 0)


def parse_log(log_path: Path, block_start: int) -> BlockInfo:
    block = BlockInfo(start_eip=block_start)
    current_section: BlockInfo | None = None
    sections: list[BlockInfo] = []
    compile_re = re.compile(
        r"^\[jit\]\s+compile block start=(?P<start>[0-9a-fA-F]+)\s+insts=(?P<insts>\d+)\s+entry=(?P<entry>0x[0-9a-fA-F]+)$"
    )
    op_re = re.compile(
        r"^\[jit\]\s+op\[(?P<idx>\d+)\]\s+handler=(?P<handler>0x[0-9a-fA-F]+)\s+sid=(?P<sid>\d+)\s+name=(?P<name>\S+)\s+code=(?P<code>\d+)\s+patches=(?P<patches>\d+)\s+branch_relocs=(?P<br>\d+)$"
    )
    compiled_re = re.compile(
        r"^\[jit\]\s+compiled block start=(?P<start>[0-9a-fA-F]+)\s+code=(?P<code>0x[0-9a-fA-F]+)\s+size=(?P<size>\d+)\s+branch_relocs=(?P<br>\d+)\s+veneers=(?P<veneers>\d+)\s+veneer_bytes=(?P<vbytes>\d+)$"
    )
    dumped_re = re.compile(
        r"^\[jit\]\s+dumped block start=(?P<start>[0-9a-fA-F]+)\s+path=(?P<path>\S+)\s+size=(?P<size>\d+)$"
    )
    reloc_direct_re = re.compile(
        r"^\[jit\]\s+reloc op\[(?P<idx>\d+)\]\s+name=(?P<name>\S+)\s+branch=(?P<branch>0x[0-9a-fA-F]+)\s+target=(?P<target>0x[0-9a-fA-F]+)\s+veneer=(?P<veneer>0x[0-9a-fA-F]+)\s+is_call=(?P<call>[01])$"
    )
    reloc_block_re = re.compile(
        r"^\[jit\]\s+reloc op\[(?P<idx>\d+)\]\s+name=(?P<name>\S+)\s+branch=(?P<branch>0x[0-9a-fA-F]+)\s+block_target_op=(?P<target_op>\d+)\s+runtime_op=(?P<runtime_op>0x[0-9a-fA-F]+)\s+target=(?P<target>0x[0-9a-fA-F]+)$"
    )
    reloc_tail_re = re.compile(
        r"^\[jit\]\s+reloc op\[(?P<idx>\d+)\]\s+name=(?P<name>\S+)\s+branch=(?P<branch>0x[0-9a-fA-F]+)\s+handler_tail\s+runtime_op=(?P<runtime_op>0x[0-9a-fA-F]+)\s+handler=(?P<handler>0x[0-9a-fA-F]+)\s+veneer=(?P<veneer>0x[0-9a-fA-F]+)$"
    )

    for line in log_path.read_text().splitlines():
        m = compile_re.match(line)
        if m:
            if current_section is not None:
                sections.append(current_section)
            start = int(m.group("start"), 16)
            current_section = BlockInfo(start_eip=start)
            continue

        m = op_re.match(line)
        if m and current_section is not None:
            current_section.ops.append(
                OpInfo(
                    index=int(m.group("idx")),
                    name=m.group("name"),
                    code_size=int(m.group("code")),
                    patches=int(m.group("patches")),
                    branch_relocs=int(m.group("br")),
                )
            )
            continue

        m = compiled_re.match(line)
        if m and current_section is not None and int(m.group("start"), 16) == current_section.start_eip:
            current_section.code_addr = parse_int(m.group("code"))
            current_section.code_size = int(m.group("size"))
            current_section.branch_relocs = int(m.group("br"))
            current_section.veneer_count = int(m.group("veneers"))
            current_section.veneer_bytes = int(m.group("vbytes"))
            sections.append(current_section)
            current_section = None
            continue

        m = dumped_re.match(line)
        if m and current_section is not None and int(m.group("start"), 16) == current_section.start_eip:
            current_section.dumped_path = m.group("path")
            continue

        m = reloc_direct_re.match(line)
        if m and current_section is not None:
            current_section.relocs.append(
                RelocInfo(
                    op_index=int(m.group("idx")),
                    name=m.group("name"),
                    branch_addr=parse_int(m.group("branch")),
                    target_addr=parse_int(m.group("target")),
                    veneer_addr=parse_int(m.group("veneer")),
                    is_call=(m.group("call") == "1"),
                    kind="direct",
                )
            )
            continue

        m = reloc_block_re.match(line)
        if m and current_section is not None:
            current_section.relocs.append(
                RelocInfo(
                    op_index=int(m.group("idx")),
                    name=m.group("name"),
                    branch_addr=parse_int(m.group("branch")),
                    target_addr=parse_int(m.group("target")),
                    block_target_op=int(m.group("target_op")),
                    runtime_op=parse_int(m.group("runtime_op")),
                    kind="block",
                )
            )
            continue

        m = reloc_tail_re.match(line)
        if m and current_section is not None:
            current_section.relocs.append(
                RelocInfo(
                    op_index=int(m.group("idx")),
                    name=m.group("name"),
                    branch_addr=parse_int(m.group("branch")),
                    runtime_op=parse_int(m.group("runtime_op")),
                    handler_addr=parse_int(m.group("handler")),
                    veneer_addr=parse_int(m.group("veneer")),
                    kind="handler_tail",
                )
            )
            continue

    if current_section is not None:
        sections.append(current_section)

    matches = [section for section in sections if section.start_eip == block_start]
    if matches:
        block = matches[-1]

    offset = 0
    for op in block.ops:
        op.start_off = offset
        op.end_off = offset + op.code_size
        offset = op.end_off
    return block


def parse_nm(nm_text: str) -> dict[int, str]:
    out: dict[int, str] = {}
    for line in nm_text.splitlines():
        m = re.match(r"^([0-9a-fA-F]+)\s+\S\s+(.+)$", line.strip())
        if not m:
            continue
        out[int(m.group(1), 16)] = m.group(2).strip()
    return out


def infer_slide(block: BlockInfo, symbols: dict[int, str]) -> int | None:
    slides: list[int] = []
    symbol_to_addr = {name: addr for addr, name in symbols.items()}
    for reloc in block.relocs:
        if reloc.target_addr is None:
            continue
        if reloc.name in symbol_to_addr:
            slides.append(reloc.target_addr - symbol_to_addr[reloc.name])
    if not slides:
        return None
    return Counter(slides).most_common(1)[0][0]


def resolve_symbol(runtime_addr: int, symbols: dict[int, str], slide: int | None) -> tuple[str | None, int | None]:
    if slide is None:
        return None, None
    image_addr = runtime_addr - slide
    if image_addr in symbols:
        return symbols[image_addr], image_addr
    return None, image_addr


def decode_mov_wide_imm(word: int) -> tuple[str, int, int]:
    sf = (word >> 31) & 1
    opc = (word >> 29) & 0b11
    fixed = (word >> 23) & 0b111111
    if fixed != 0b100101:
        raise ValueError("not mov wide")
    hw = (word >> 21) & 0b11
    imm16 = (word >> 5) & 0xFFFF
    rd = word & 0x1F
    if sf != 1:
        raise ValueError("not 64-bit mov wide")
    if opc == 0b10:
        op = "movz"
    elif opc == 0b11:
        op = "movk"
    elif opc == 0b00:
        op = "movn"
    else:
        raise ValueError("unknown mov wide opc")
    return op, imm16, rd | (hw << 8)


def is_mov_wide_seq(words: list[int], reg: int, ops: list[str]) -> bool:
    if len(words) < len(ops):
        return False
    for word, want in zip(words, ops):
        try:
            op, _imm16, rd_hw = decode_mov_wide_imm(word)
        except Exception:
            return False
        rd = rd_hw & 0x1F
        if rd != reg or op != want:
            return False
    return True


def decode_veneer_target(words: list[int], reg: int) -> int:
    value = 0
    for idx, word in enumerate(words):
        op, imm16, rd_hw = decode_mov_wide_imm(word)
        rd = rd_hw & 0x1F
        hw = rd_hw >> 8
        if rd != reg:
            raise ValueError(f"unexpected register x{rd}, wanted x{reg}")
        if idx == 0 and op != "movz":
            raise ValueError("veneer does not start with movz")
        if idx > 0 and op != "movk":
            raise ValueError("veneer continuation is not movk")
        value |= imm16 << (16 * hw)
    return value


def decode_veneer(blob: bytes, off: int) -> dict[str, int | str]:
    def u32(at: int) -> int:
        return int.from_bytes(blob[at:at + 4], "little")

    if off + 24 > len(blob):
        raise ValueError("truncated veneer")

    words6 = [u32(off + i * 4) for i in range(6)]
    words8 = [u32(off + i * 4) for i in range(8)]
    words10 = [u32(off + i * 4) for i in range(10)]

    if (
        is_mov_wide_seq(words10[0:4], 1, ["movz", "movk", "movk", "movk"])
        and is_mov_wide_seq(words10[4:8], 16, ["movz", "movk", "movk", "movk"])
        and words10[8] == 0xD61F0200
    ):
        op_addr = decode_veneer_target(words10[0:4], 1)
        target = decode_veneer_target(words10[4:8], 16)
        return {"kind": "handler_tail", "size": 40, "target": target, "op_addr": op_addr}

    if (
        words8[0] == 0xF81F0FFE
        and is_mov_wide_seq(words8[1:5], 16, ["movz", "movk", "movk", "movk"])
        and words8[5] == 0xD63F0200
        and words8[6] == 0xF84107FE
        and words8[7] == 0xD65F03C0
    ):
        target = decode_veneer_target(words8[1:5], 16)
        return {"kind": "call", "size": 32, "target": target}

    if is_mov_wide_seq(words6[0:4], 16, ["movz", "movk", "movk", "movk"]) and words6[4] == 0xD61F0200:
        target = decode_veneer_target(words6[0:4], 16)
        return {"kind": "jump", "size": 24, "target": target}

    raise ValueError("unknown veneer template")


def make_disassembly(dump_bin: Path, dis_path: Path) -> None:
    blob = dump_bin.read_bytes()
    with tempfile.TemporaryDirectory() as td:
        asm_path = Path(td) / "jit_dump.s"
        obj_path = Path(td) / "jit_dump.o"
        with asm_path.open("w") as f:
            f.write(".text\n.globl _jit_dump\n_jit_dump:\n")
            for i in range(0, len(blob), 4):
                chunk = blob[i:i + 4]
                if len(chunk) == 4:
                    f.write(f"  .long 0x{int.from_bytes(chunk, 'little'):08x}\n")
        run(["xcrun", "clang", "-target", "arm64-apple-macos14", "-c", str(asm_path), "-o", str(obj_path)])
        text = run(["xcrun", "llvm-objdump", "-d", "--symbolize-operands", "--no-show-raw-insn", str(obj_path)])
        dis_path.write_text(text)


def parse_dis_lines(dis_path: Path) -> list[tuple[int, str]]:
    lines: list[tuple[int, str]] = []
    for line in dis_path.read_text().splitlines():
        m = re.match(r"^\s*([0-9a-fA-F]+):\s+(.*)$", line)
        if m:
            lines.append((int(m.group(1), 16), line))
    return lines


def maybe_demangle(name: str) -> str:
    if not name or not name.startswith("_Z"):
        return name
    try:
        return run(["c++filt", name], check=False).strip() or name
    except Exception:
        return name


def build_report(block: BlockInfo, blob: bytes, symbols: dict[int, str], slide: int | None, dis_lines: list[tuple[int, str]]) -> str:
    if block.code_size is None:
        block.code_size = len(blob)
    code_bytes = sum(op.code_size for op in block.ops)
    veneer_start = code_bytes + 4
    lines: list[str] = []
    lines.append(f"Block 0x{block.start_eip:08x}")
    if block.code_addr is not None:
        lines.append(f"Runtime code base: 0x{block.code_addr:x}")
    lines.append(f"Dump size: {len(blob)} bytes")
    lines.append(f"Code bytes before ret/veneers: {code_bytes}")
    lines.append(f"Veneer region starts at: 0x{veneer_start:x}")
    if slide is not None:
        lines.append(f"Inferred dylib slide: 0x{slide:x}")
    if block.veneer_count is not None:
        lines.append(f"Logged veneers: {block.veneer_count} ({block.veneer_bytes} bytes)")
    lines.append("")

    if block.ops:
        lines.append("Ops:")
        for op in block.ops:
            lines.append(
                f"  op[{op.index:2d}] 0x{op.start_off:04x}..0x{op.end_off:04x}  {op.name}  code={op.code_size} patches={op.patches} branch_relocs={op.branch_relocs}"
            )
        lines.append("")

    lines.append("Veneers:")
    veneer_off = veneer_start
    decoded_veneers: dict[int, dict[str, int | str]] = {}
    while veneer_off + 4 <= len(blob):
        if veneer_off + 4 <= len(blob) and int.from_bytes(blob[veneer_off:veneer_off + 4], "little") == 0:
            break
        try:
            info = decode_veneer(blob, veneer_off)
        except Exception:
            lines.append(f"  0x{veneer_off:04x}  <unrecognized veneer bytes>")
            break
        decoded_veneers[veneer_off] = info
        target = int(info["target"])
        sym, img = resolve_symbol(target, symbols, slide)
        extra = ""
        if info["kind"] == "handler_tail":
            extra = f" op=0x{int(info['op_addr']):x}"
        sym_text = maybe_demangle(sym) if sym else "<unknown>"
        img_text = f" image=0x{img:x}" if img is not None else ""
        lines.append(f"  0x{veneer_off:04x}  {info['kind']:12s} -> 0x{target:x} {sym_text}{img_text}{extra}")
        veneer_off += int(info["size"])
    lines.append("")

    if block.relocs:
        lines.append("Relocations:")
        for reloc in block.relocs:
            branch_off = reloc.branch_addr - block.code_addr if block.code_addr is not None else None
            veneer_off_rel = (
                reloc.veneer_addr - block.code_addr
                if reloc.veneer_addr is not None and block.code_addr is not None
                else None
            )
            text = f"  op[{reloc.op_index:2d}] {reloc.name}"
            if branch_off is not None:
                text += f" branch=0x{branch_off:04x}"
            if reloc.kind == "block":
                text += f" -> block_target_op={reloc.block_target_op}"
            elif reloc.kind == "handler_tail":
                text += f" -> handler_tail handler=0x{reloc.handler_addr:x}"
            else:
                target_sym, img = resolve_symbol(reloc.target_addr or 0, symbols, slide)
                if reloc.target_addr is not None:
                    target_desc = f"0x{reloc.target_addr:x}"
                    if target_sym:
                        target_desc += f" ({maybe_demangle(target_sym)})"
                    elif img is not None:
                        target_desc += f" (image=0x{img:x})"
                    text += f" -> {target_desc}"
            if veneer_off_rel is not None:
                text += f" veneer=0x{veneer_off_rel:04x}"
            lines.append(text)
        lines.append("")

    lines.append("Annotated disassembly:")
    op_markers = {op.start_off: f"; ---- op[{op.index}] {op.name} ----" for op in block.ops}
    if code_bytes < len(blob):
        op_markers[code_bytes] = "; ---- block ret ----"
    for off, info in decoded_veneers.items():
        target = int(info["target"])
        sym, _ = resolve_symbol(target, symbols, slide)
        tag = f"; ---- veneer {info['kind']} -> 0x{target:x}"
        if sym:
            tag += f" ({maybe_demangle(sym)})"
        if info["kind"] == "handler_tail":
            tag += f" op=0x{int(info['op_addr']):x}"
        tag += " ----"
        op_markers[off] = tag

    for off, line in dis_lines:
        marker = op_markers.get(off)
        if marker:
            lines.append(marker)
        lines.append(line)
    lines.append("")
    return "\n".join(lines)


def default_dylib() -> Path | None:
    candidates = [
        Path("Podish.Cli/bin/Debug/net10.0/libfibercpu.dylib"),
        Path("Fiberish.X86/bin/Debug/net10.0/libfibercpu.dylib"),
        Path("Fiberish.X86/build_native/host/libfibercpu.dylib"),
    ]
    for c in candidates:
        if c.exists():
            return c
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Annotate a JIT block dump and resolve veneer targets back to symbols.")
    parser.add_argument("dump_bin", help="Path to jit_xxxxxxxx.bin")
    parser.add_argument("--block", help="Block start EIP (hex or dec). Defaults to jit_XXXXXXXX.bin name.")
    parser.add_argument("--log", default="/tmp/fibercpu_jit.log", help="JIT log path")
    parser.add_argument("--dylib", help="libfibercpu path used for nm symbol lookup")
    parser.add_argument("--dis", help="Existing disassembly path. If omitted, script will create one next to the dump.")
    parser.add_argument("--output", help="Output report path. Default: <dump>.annotated.txt")
    args = parser.parse_args()

    dump_bin = Path(args.dump_bin)
    if not dump_bin.exists():
        raise SystemExit(f"dump not found: {dump_bin}")

    if args.block:
        block_start = parse_int(args.block)
    else:
        m = re.search(r"jit_([0-9a-fA-F]+)\.bin$", dump_bin.name)
        if not m:
            raise SystemExit("could not infer block start from dump filename; pass --block")
        block_start = int(m.group(1), 16)

    log_path = Path(args.log)
    block = parse_log(log_path, block_start) if log_path.exists() else BlockInfo(start_eip=block_start)

    dylib = Path(args.dylib) if args.dylib else default_dylib()
    if dylib is None or not dylib.exists():
        raise SystemExit("could not find libfibercpu.dylib; pass --dylib")

    nm_text = run(["nm", "-a", str(dylib)])
    symbols = parse_nm(nm_text)
    slide = infer_slide(block, symbols)

    dis_path = Path(args.dis) if args.dis else dump_bin.with_suffix(".dis")
    if not dis_path.exists():
        make_disassembly(dump_bin, dis_path)
    dis_lines = parse_dis_lines(dis_path)

    blob = dump_bin.read_bytes()
    report = build_report(block, blob, symbols, slide, dis_lines)
    out_path = Path(args.output) if args.output else dump_bin.with_suffix(".annotated.txt")
    out_path.write_text(report)
    print(out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())

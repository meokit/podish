import sys
import subprocess
import re
import os
import json
import argparse

# Kind enum matching C++ PatchKind
PATCH_KINDS = [
    "OpQword64"
]
PATCH_MAGIC_HIGH = 0xABCD
PATCH_MAGIC64_MID = 0xEF01
PATCH_MAGIC64_HIGH = 0x2345

SYMBOL_LINE_RE = re.compile(r"^\s*[0-9a-fA-F]+ <(.+)>:$")
ADDR_SYMBOL_LINE_RE = re.compile(r"^\s*([0-9a-fA-F]+) <(.+)>:$")
PROBE_KERNEL_OP_RE = re.compile(
    r"ProbeKernel<&fiberish::op::(Op[a-zA-Z0-9_]+)\("
)
RET_IMM16_RE = re.compile(r"ProbeKernel<&fiberish::op::OpRet_Imm16\(")
MEM_ACCESS_RE = re.compile(
    r"\bldr(?:b|h|sb)?\s+([wx]\d+),\s+\[(x\d+)(?:,\s+#(0x[0-9a-fA-F]+|\d+))?\]"
)
PAIR_ACCESS_RE = re.compile(
    r"\bldp\s+([wx]\d+),\s+([wx]\d+),\s+\[(x\d+)(?:,\s+#(0x[0-9a-fA-F]+|\d+))?\]"
)
RELOC_LINE_RE = re.compile(r"^\s*([0-9a-fA-F]+)\s+([A-Z0-9_]+)\s+(.+?)\s*$")

def decode_move_wide(inst):
    sf = (inst >> 31) & 0x1
    opc = (inst >> 29) & 0x3
    fixed = (inst >> 23) & 0x3F
    hw = (inst >> 21) & 0x3
    imm16 = (inst >> 5) & 0xFFFF
    rd = inst & 0x1F
    if fixed != 0b100101:
        return None
    if sf != 1:
        return None
    if opc == 0b10:
        kind = "movz"
    elif opc == 0b11:
        kind = "movk"
    else:
        return None
    return {
        "kind": kind,
        "hw": hw,
        "imm16": imm16,
        "rd": rd,
    }

def parse_disassembly(obj_path):
    print(f"Analyzing probe object {obj_path}...")
    try:
        res = subprocess.run(["llvm-objdump", "-d", "--demangle", obj_path], capture_output=True, text=True)
        if res.returncode != 0:
            raise FileNotFoundError()
    except:
        res = subprocess.run(["objdump", "-d", "--demangle", obj_path], capture_output=True, text=True)
    return res.stdout

def split_functions(disasm_text):
    functions = []
    current_name = None
    current_lines = []
    for raw_line in disasm_text.splitlines():
        m = SYMBOL_LINE_RE.match(raw_line)
        if m:
            if current_name is not None:
                functions.append((current_name, current_lines))
            current_name = m.group(1)
            current_lines = []
            continue
        if current_name is not None:
            current_lines.append(raw_line.strip())
    if current_name is not None:
        functions.append((current_name, current_lines))
    return functions

def parse_offset(offset_text):
    if not offset_text:
        return 0
    return int(offset_text, 0)

def detect_op_base_register(functions):
    for name, lines in functions:
        if not RET_IMM16_RE.search(name):
            continue
        for line in lines:
            m = MEM_ACCESS_RE.search(line)
            if not m:
                continue
            base_reg = m.group(2)
            offset = parse_offset(m.group(3))
            if offset in (0xF, 0x10):
                return base_reg
    return None

def record_qword_access(target_usage, offset, width_bits):
    start = offset
    end = offset + (width_bits // 8)
    for qword_index in (1, 2, 3):
        qword_start = qword_index * 8
        qword_end = qword_start + 8
        if start < qword_end and end > qword_start:
            target_usage[f"qword{qword_index}"] = True

def analyze_probe(obj_path, out_json_path):
    disasm_text = parse_disassembly(obj_path)
    functions = split_functions(disasm_text)
    op_base_reg = detect_op_base_register(functions)
    if not op_base_reg:
        raise RuntimeError("Could not detect DecodedOp base register from OpRet_Imm16 probe")
    print(f"Detected DecodedOp base register: {op_base_reg}")

    usage_map = {}
    for name, lines in functions:
        m_probe = PROBE_KERNEL_OP_RE.search(name)
        if not m_probe:
            continue
        current_target = m_probe.group(1)
        usage_map[current_target] = {}
        helper_call = False

        for line in lines:
            if re.search(r"\bbl(?:r)?\b", line):
                helper_call = True

            m_mem = MEM_ACCESS_RE.search(line)
            if m_mem and m_mem.group(2) == op_base_reg:
                dest_reg = m_mem.group(1)
                offset = parse_offset(m_mem.group(3))
                record_qword_access(usage_map[current_target], offset, 64 if dest_reg.startswith("x") else 32)

            m_pair = PAIR_ACCESS_RE.search(line)
            if m_pair and m_pair.group(3) == op_base_reg:
                base_offset = parse_offset(m_pair.group(4))
                width_bits = 64 if m_pair.group(1).startswith("x") else 32
                stride = 8 if width_bits == 64 else 4
                for offset in (base_offset, base_offset + stride):
                    record_qword_access(usage_map[current_target], offset, width_bits)

        if helper_call:
            usage_map[current_target]["qword1"] = True
            usage_map[current_target]["qword2"] = True
            usage_map[current_target]["qword3"] = True

    print(f"Analysis complete. Found usage for {len(usage_map)} opcodes.")
    with open(out_json_path, "w") as f:
        json.dump(usage_map, f, indent=2)

def extract_stencils(obj_path, out_path, usage_path=None):
    print(f"Extracting stencils from {obj_path}...")
    try:
        res = subprocess.run(["xcrun", "llvm-objdump", "-d", "--demangle", obj_path], capture_output=True, text=True)
    except:
        res = subprocess.run(["objdump", "-d", "--demangle", obj_path], capture_output=True, text=True)
    try:
        reloc_res = subprocess.run(["xcrun", "llvm-objdump", "-r", obj_path], capture_output=True, text=True)
    except:
        reloc_res = subprocess.run(["objdump", "-r", obj_path], capture_output=True, text=True)

    symbol_regex = re.compile(r'fiberish::jit::ExtractKernel<(.*)>')
    op_regex = re.compile(r'fiberish::op::(Op[a-zA-Z0-9_]+)')
    inst_regex = re.compile(r'^\s*([0-9a-fA-F]+):\s+([0-9a-fA-F]{8})')

    stencils = {}
    current_symbol = None
    current_start = None
    usage_data = {}
    if usage_path and os.path.exists(usage_path):
        with open(usage_path, "r") as f:
            usage_data = json.load(f)

    for line in res.stdout.splitlines():
        m_header = ADDR_SYMBOL_LINE_RE.match(line)
        if m_header:
            current_symbol = None
            current_start = None
            m_sym = symbol_regex.search(m_header.group(2))
            if not m_sym:
                continue
            m_op = op_regex.search(m_sym.group(1))
            if m_op:
                current_symbol = m_op.group(1)
                if current_symbol not in stencils:
                    stencils[current_symbol] = {
                        'bytes': bytearray(),
                        'patches': [],
                        'branch_relocs': [],
                        'start': int(m_header.group(1), 16),
                    }
                current_start = stencils[current_symbol]['start']
            continue
            
        if current_symbol:
            m_inst = inst_regex.search(line)
            if m_inst:
                inst_hex = m_inst.group(2)
                inst_val = int(inst_hex, 16)
                stencils[current_symbol]['bytes'].extend(inst_val.to_bytes(4, byteorder='little'))
            elif line.strip() == "" or (line.endswith(">:") and not symbol_regex.search(line)):
                current_symbol = None
                current_start = None

    branch_target_ids = {}
    branch_targets = []
    relocations = []
    for line in reloc_res.stdout.splitlines():
        m = RELOC_LINE_RE.match(line)
        if not m:
            continue
        reloc_type = m.group(2)
        if reloc_type != "ARM64_RELOC_BRANCH26":
            continue
        relocations.append({
            "offset": int(m.group(1), 16),
            "symbol": m.group(3),
        })

    expected_branch_relocs = {name: 0 for name in stencils.keys()}
    skipped_relocs = 0
    for reloc in relocations:
        reloc_offset = reloc["offset"]
        matched = False
        for data in stencils.values():
            start = data["start"]
            end = start + len(data["bytes"])
            if reloc_offset < start or reloc_offset >= end:
                continue
            expected_branch_relocs[next(name for name, item in stencils.items() if item is data)] += 1
            symbol = reloc["symbol"]
            target_id = branch_target_ids.get(symbol)
            if target_id is None:
                target_id = len(branch_targets)
                branch_target_ids[symbol] = target_id
                branch_targets.append(symbol)
            data["branch_relocs"].append({
                "offset": reloc_offset - start,
                "target_id": target_id,
            })
            matched = True
            break
        if not matched:
            skipped_relocs += 1

    if skipped_relocs:
        print(f"Skipped {skipped_relocs} branch relocations outside extracted stencil bodies.")

    print(f"Found {len(stencils)} stencil candidates. Scanning for patches...")
    for name, data in stencils.items():
        code = data['bytes']
        i = 0
        while i < len(code) - 15:
            inst1 = int.from_bytes(code[i:i+4], 'little')
            inst2 = int.from_bytes(code[i+4:i+8], 'little')
            inst3 = int.from_bytes(code[i+8:i+12], 'little')
            inst4 = int.from_bytes(code[i+12:i+16], 'little')
            dec1 = decode_move_wide(inst1)
            dec2 = decode_move_wide(inst2)
            dec3 = decode_move_wide(inst3)
            dec4 = decode_move_wide(inst4)
            if dec1 and dec2 and dec3 and dec4:
                if (
                    dec1["kind"] == "movz" and dec1["hw"] == 0 and
                    dec2["kind"] == "movk" and dec2["hw"] == 1 and
                    dec3["kind"] == "movk" and dec3["hw"] == 2 and
                    dec4["kind"] == "movk" and dec4["hw"] == 3
                ):
                    rd1, rd2, rd3, rd4 = dec1["rd"], dec2["rd"], dec3["rd"], dec4["rd"]
                    imm1, imm2, imm3, imm4 = dec1["imm16"], dec2["imm16"], dec3["imm16"], dec4["imm16"]
                    if rd1 == rd2 == rd3 == rd4 and imm2 == PATCH_MAGIC_HIGH and imm3 == PATCH_MAGIC64_MID and imm4 == PATCH_MAGIC64_HIGH:
                        kind_val, aux_val = imm1 & 0xFF, (imm1 >> 8) & 0xFF
                        kind = PATCH_KINDS[kind_val] if kind_val < len(PATCH_KINDS) else "Unknown"
                        data['patches'].append({'offset': i, 'kind': kind, 'aux': aux_val})
                        i += 16
                        continue
            i += 4

        expected_patch_count = sum(
            1 for key in ("qword1", "qword2", "qword3")
            if bool(usage_data.get(name, {}).get(key))
        )
        actual_patch_count = sum(1 for patch in data['patches'] if patch['kind'] == "OpQword64")
        if actual_patch_count != expected_patch_count:
            raise RuntimeError(
                f"Stencil patch mismatch for {name}: expected {expected_patch_count} qword patches, found {actual_patch_count}"
            )
        expected_reloc_count = expected_branch_relocs.get(name, 0)
        actual_reloc_count = len(data['branch_relocs'])
        if actual_reloc_count != expected_reloc_count:
            raise RuntimeError(
                f"Stencil branch reloc mismatch for {name}: expected {expected_reloc_count}, found {actual_reloc_count}"
            )
            
    with open(out_path, "w") as f:
        f.write("#pragma once\n#include \"jit/stencil.h\"\n#include \"decoder.h\"\n\n")
        f.write('extern "C" {\n')
        for name in sorted(stencils.keys()):
            f.write(f"    extern ::fiberish::HandlerFunc JitStencilHandler_{name};\n")
        for i, symbol in enumerate(branch_targets):
            f.write(f"    extern void JitBranchRelocTarget_{i}() asm(\"{symbol}\");\n")
        f.write("}\n\nnamespace fiberish::jit::generated {\n\n")
        if branch_targets:
            f.write("const void* const branch_reloc_targets[] = {\n")
            for i in range(len(branch_targets)):
                f.write(f"    reinterpret_cast<const void*>(&JitBranchRelocTarget_{i}),\n")
            f.write("};\n\n")
        else:
            f.write("const void* const* branch_reloc_targets = nullptr;\n\n")
        for name in sorted(stencils.keys()):
            data = stencils[name]
            f.write(f"const uint8_t stencil_bytes_{name}[] = {{ " + ", ".join([f"0x{b:02x}" for b in data['bytes']]) + " };\n")
            if data['patches']:
                f.write(f"const PatchDesc stencil_patches_{name}[] = {{\n")
                for p in data['patches']:
                    f.write(f"    {{ {p['offset']}, PatchKind::{p['kind']}, {p['aux']} }},\n")
                f.write("};\n")
            else: f.write(f"const PatchDesc* stencil_patches_{name} = nullptr;\n")
            if data['branch_relocs']:
                f.write(f"const BranchRelocDesc stencil_branch_relocs_{name}[] = {{\n")
                for r in data['branch_relocs']:
                    f.write(f"    {{ {r['offset']}, {r['target_id']} }},\n")
                f.write("};\n")
            else:
                f.write(f"const BranchRelocDesc* stencil_branch_relocs_{name} = nullptr;\n")
            
        f.write("\nconst StencilDesc stencils[] = {\n")
        for i, name in enumerate(sorted(stencils.keys())):
            data = stencils[name]
            p_ptr = f"stencil_patches_{name}" if data['patches'] else "nullptr"
            r_ptr = f"stencil_branch_relocs_{name}" if data['branch_relocs'] else "nullptr"
            f.write(f"    {{ stencil_bytes_{name}, sizeof(stencil_bytes_{name}), {p_ptr}, {len(data['patches'])}, {r_ptr}, {len(data['branch_relocs'])}, {i}, 0 }}, // {name}\n")
        f.write("};\n\nstruct HandlerStencilMapEntry { ::fiberish::HandlerFunc target; uint16_t stencil_id; };\n")
        f.write("const HandlerStencilMapEntry handler_to_stencil[] = {\n")
        for i, name in enumerate(sorted(stencils.keys())):
            f.write(f"    {{ JitStencilHandler_{name}, {i} }},\n")
        f.write("};\nconst size_t handler_to_stencil_count = " + str(len(stencils)) + ";\n} // namespace fiberish::jit::generated\n")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("input", help="Object file path")
    parser.add_argument("output", help="Output path (.json or .inc)")
    parser.add_argument("--mode", choices=["analyze", "extract"], default="extract")
    parser.add_argument("--usage", help="Usage JSON path for expected patch validation", default=None)
    args = parser.parse_args()
    if args.mode == "analyze":
        analyze_probe(args.input, args.output)
    else:
        extract_stencils(args.input, args.output, args.usage)

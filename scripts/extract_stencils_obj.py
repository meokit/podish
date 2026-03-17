import sys
import subprocess
import re
import os
import json
import argparse
from collections import defaultdict

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
NM_LINE_RE = re.compile(r"^([0-9a-fA-F]+)\s+([A-Za-z])\s+(.+)$")

SECTION_NAMES_TO_LOAD = {
    ("__TEXT", "__text"),
    ("__TEXT", "__literal16"),
    ("__TEXT", "__const"),
    ("__TEXT", "__literal8"),
    ("__TEXT", "__cstring"),
    ("__DATA", "__const"),
    ("__DATA", "__data"),
}

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

def parse_sections(obj_path):
    res = subprocess.run(["otool", "-l", obj_path], capture_output=True, text=True, check=True)
    sections = []
    current = {}
    in_section = False
    for raw_line in res.stdout.splitlines():
        line = raw_line.strip()
        if line == "Section":
            if in_section and current:
                sections.append(current)
            current = {}
            in_section = True
            continue
        if not in_section:
            continue
        parts = line.split()
        if len(parts) >= 2:
            key = parts[0]
            value = parts[1]
            if key in {"sectname", "segname"}:
                current[key] = value
            elif key in {"addr", "size", "offset"}:
                current[key] = int(value, 16 if value.startswith("0x") else 10)
    if in_section and current:
        sections.append(current)
    return sections

def parse_symbols(obj_path):
    res = subprocess.run(["nm", "-a", "-n", obj_path], capture_output=True, text=True, check=True)
    symbols = []
    for line in res.stdout.splitlines():
        m = NM_LINE_RE.match(line.strip())
        if not m:
            continue
        symbols.append({
            "addr": int(m.group(1), 16),
            "type": m.group(2),
            "name": m.group(3),
        })
    return symbols

def load_section_bytes(obj_path, sections):
    section_bytes = {}
    with open(obj_path, "rb") as f:
        blob = f.read()
    for sec in sections:
        key = (sec.get("segname"), sec.get("sectname"))
        if key not in SECTION_NAMES_TO_LOAD:
            continue
        file_off = sec["offset"]
        size = sec["size"]
        section_bytes[key] = blob[file_off:file_off + size]
    return section_bytes

def find_section_for_addr(sections, addr):
    for sec in sections:
        start = sec.get("addr")
        size = sec.get("size")
        if start is None or size is None:
            continue
        if start <= addr < start + size:
            return sec
    return None

def infer_symbol_size(symbol, symbols, sections):
    section = find_section_for_addr(sections, symbol["addr"])
    if section is None:
        return 0
    next_addr = section["addr"] + section["size"]
    for other in symbols:
        if other["addr"] <= symbol["addr"]:
            continue
        if find_section_for_addr(sections, other["addr"]) == section:
            next_addr = other["addr"]
            break
    return max(0, next_addr - symbol["addr"])

def extract_local_symbol_data(symbol, symbols, sections, section_bytes):
    section = find_section_for_addr(sections, symbol["addr"])
    if section is None:
        raise RuntimeError(f"Could not find section for local symbol {symbol['name']}")
    sec_key = (section["segname"], section["sectname"])
    blob = section_bytes.get(sec_key)
    if blob is None:
        raise RuntimeError(f"Section bytes unavailable for local symbol {symbol['name']} in {sec_key}")
    section_offset = symbol["addr"] - section["addr"]
    if section["sectname"] == "__cstring":
        end = blob.find(b"\x00", section_offset)
        if end < 0:
            raise RuntimeError(f"Unterminated cstring for local symbol {symbol['name']}")
        end += 1
    else:
        size = infer_symbol_size(symbol, symbols, sections)
        if size <= 0:
            raise RuntimeError(f"Could not infer size for local symbol {symbol['name']}")
        end = section_offset + size
    return bytes(blob[section_offset:end])

def trailing_alignment(addr, max_align=16):
    align = 1
    while align < max_align and (addr & align) == 0:
        align <<= 1
    return min(align, max_align)

def decode_add_immediate(inst):
    if (inst & 0x7F000000) != 0x11000000:
        return None
    if ((inst >> 24) & 0x1) != 1:
        return None
    sf = (inst >> 31) & 0x1
    op = (inst >> 30) & 0x1
    if sf != 1 or op != 0:
        return None
    shift = (inst >> 22) & 0x3
    if shift != 0:
        return None
    rn = (inst >> 5) & 0x1F
    rd = inst & 0x1F
    return {"rn": rn, "rd": rd}

def decode_adrp(inst):
    if (inst & 0x9F000000) != 0x90000000:
        return None
    return {"rd": inst & 0x1F}

def decode_load_store_unsigned(inst):
    if (inst & 0x3B000000) != 0x39000000:
        return None
    size_field = (inst >> 30) & 0x3
    opc = (inst >> 22) & 0x3
    v = (inst >> 26) & 0x1
    rn = (inst >> 5) & 0x1F
    rt = inst & 0x1F
    if v == 0:
        access_size = 1 << size_field
        return {"rn": rn, "rt": rt, "shift": size_field, "access_size": access_size, "is_vector": False}
    # For SIMD&FP unsigned-offset loads/stores, the immediate is scaled by
    # the datum size encoded by opc:size. Examples:
    #   ldr b  -> scale 0
    #   ldr h  -> scale 1
    #   ldr s  -> scale 2
    #   ldr d  -> scale 3
    #   ldr q  -> scale 4
    shift = (((opc >> 1) & 0x1) << 2) | size_field
    access_size = 1 << shift
    return {"rn": rn, "rt": rt, "shift": shift, "access_size": access_size, "is_vector": True}

def classify_addr_reloc_pair(page21_type, pageoff_type, adrp_inst, pageoff_inst):
    adrp = decode_adrp(adrp_inst)
    if adrp is None:
        raise RuntimeError(f"Expected ADRP at page21 relocation, got 0x{adrp_inst:08x}")
    if page21_type == "ARM64_RELOC_GOT_LOAD_PAGE21":
        load = decode_load_store_unsigned(pageoff_inst)
        if load is None or load["rn"] != adrp["rd"] or load["rt"] != adrp["rd"]:
            raise RuntimeError(f"Expected GOT load pair after ADRP, got 0x{pageoff_inst:08x}")
        return ("GotLoadToAddr", 0)
    add = decode_add_immediate(pageoff_inst)
    if add is not None and add["rn"] == adrp["rd"] and add["rd"] == adrp["rd"]:
        return ("PageOffset", 0)
    load = decode_load_store_unsigned(pageoff_inst)
    if load is not None and load["rn"] == adrp["rd"]:
        return ("PageOffset", load["shift"])
    raise RuntimeError(f"Unsupported PAGEOFF12 relocation pair: page21={page21_type}, inst=0x{pageoff_inst:08x}")

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

    symbol_regex = re.compile(r'fiberish::jit::(?:ExtractKernel|JitDispatchKernel)<(.*)>')
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
                        'addr_relocs': [],
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

    sections = parse_sections(obj_path)
    symbols = parse_symbols(obj_path)
    symbol_by_name = {symbol["name"]: symbol for symbol in symbols}
    section_bytes = load_section_bytes(obj_path, sections)

    branch_target_ids = {}
    branch_targets = []
    addr_target_ids = {}
    addr_targets = []
    relocations = []
    for line in reloc_res.stdout.splitlines():
        m = RELOC_LINE_RE.match(line)
        if not m:
            continue
        relocations.append({
            "offset": int(m.group(1), 16),
            "type": m.group(2),
            "symbol": m.group(3),
        })

    expected_branch_relocs = {name: 0 for name in stencils.keys()}
    expected_addr_relocs = {name: 0 for name in stencils.keys()}
    skipped_relocs = 0
    stencil_items = sorted(stencils.items(), key=lambda item: item[1]["start"])

    def find_stencil_for_offset(reloc_offset):
        for stencil_name, data in stencil_items:
            start = data["start"]
            end = start + len(data["bytes"])
            if start <= reloc_offset < end:
                return stencil_name, data
        return None, None

    def intern_addr_target(symbol):
        target_id = addr_target_ids.get(symbol)
        if target_id is not None:
            return target_id
        entry = {
            "name": symbol,
            "storage": "external",
        }
        sym = symbol_by_name.get(symbol)
        if sym and sym["type"].islower():
            entry["storage"] = "embedded"
            entry["bytes"] = extract_local_symbol_data(sym, symbols, sections, section_bytes)
            entry["align"] = trailing_alignment(sym["addr"])
        target_id = len(addr_targets)
        addr_target_ids[symbol] = target_id
        addr_targets.append(entry)
        return target_id

    branch_relocs = []
    addr_reloc_candidates = defaultdict(lambda: defaultdict(list))
    for reloc in relocations:
        reloc_offset = reloc["offset"]
        stencil_name, data = find_stencil_for_offset(reloc_offset)
        if data is None:
            skipped_relocs += 1
            continue
        start = data["start"]
        reloc_type = reloc["type"]
        symbol = reloc["symbol"]
        if reloc_type == "ARM64_RELOC_BRANCH26":
            expected_branch_relocs[stencil_name] += 1
            target_id = branch_target_ids.get(symbol)
            if target_id is None:
                target_id = len(branch_targets)
                branch_target_ids[symbol] = target_id
                branch_targets.append(symbol)
            data["branch_relocs"].append({
                "offset": reloc_offset - start,
                "target_id": target_id,
            })
            branch_relocs.append(reloc)
            continue
        if reloc_type in {
            "ARM64_RELOC_PAGE21",
            "ARM64_RELOC_PAGEOFF12",
            "ARM64_RELOC_GOT_LOAD_PAGE21",
            "ARM64_RELOC_GOT_LOAD_PAGEOFF12",
        }:
            candidate = addr_reloc_candidates[(stencil_name, symbol)]
            candidate[reloc_type].append(reloc_offset - start)
            continue

    for (stencil_name, symbol), candidate in sorted(addr_reloc_candidates.items()):
        data = stencils[stencil_name]
        start = data["start"]
        pair_kinds = [
            ("ARM64_RELOC_PAGE21", "ARM64_RELOC_PAGEOFF12"),
            ("ARM64_RELOC_GOT_LOAD_PAGE21", "ARM64_RELOC_GOT_LOAD_PAGEOFF12"),
        ]
        matched_any = False
        for page21_type, pageoff_type in pair_kinds:
            page21_offsets = sorted(candidate.get(page21_type, []))
            pageoff_offsets = sorted(candidate.get(pageoff_type, []))
            if not page21_offsets and not pageoff_offsets:
                continue
            matched_any = True
            if len(page21_offsets) != len(pageoff_offsets):
                raise RuntimeError(
                    f"Mismatched address relocation pair counts for {stencil_name}:{symbol}: "
                    f"{page21_type}={len(page21_offsets)} {pageoff_type}={len(pageoff_offsets)}"
                )
            for page21_offset, pageoff_offset in zip(page21_offsets, pageoff_offsets):
                adrp_inst = int.from_bytes(data["bytes"][page21_offset:page21_offset + 4], "little")
                pageoff_inst = int.from_bytes(data["bytes"][pageoff_offset:pageoff_offset + 4], "little")
                kind, shift = classify_addr_reloc_pair(page21_type, pageoff_type, adrp_inst, pageoff_inst)
                target_id = intern_addr_target(symbol)
                data["addr_relocs"].append({
                    "page21_offset": page21_offset,
                    "pageoff12_offset": pageoff_offset,
                    "target_id": target_id,
                    "kind": kind,
                    "shift": shift,
                })
                expected_addr_relocs[stencil_name] += 1
        if not matched_any:
            raise RuntimeError(
                f"Incomplete address relocation pair for {stencil_name}:{symbol}: {sorted(candidate.keys())}"
            )

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
        expected_addr_reloc_count = expected_addr_relocs.get(name, 0)
        actual_addr_reloc_count = len(data.get('addr_relocs', []))
        if actual_addr_reloc_count != expected_addr_reloc_count:
            raise RuntimeError(
                f"Stencil address reloc mismatch for {name}: expected {expected_addr_reloc_count}, found {actual_addr_reloc_count}"
            )
            
    with open(out_path, "w") as f:
        f.write("#pragma once\n#include \"jit/stencil.h\"\n#include \"decoder.h\"\n\n")
        f.write('extern "C" {\n')
        for name in sorted(stencils.keys()):
            f.write(f"    extern ::fiberish::HandlerFunc JitStencilHandler_{name};\n")
        for i, symbol in enumerate(branch_targets):
            f.write(f"    extern void JitBranchRelocTarget_{i}() asm(\"{symbol}\");\n")
        for i, target in enumerate(addr_targets):
            if target["storage"] == "external":
                f.write(f"    extern const unsigned char JitAddrRelocTarget_{i} asm(\"{target['name']}\");\n")
        f.write("}\n\nnamespace fiberish::jit::generated {\n\n")
        if branch_targets:
            f.write("const void* const branch_reloc_targets[] = {\n")
            for i in range(len(branch_targets)):
                f.write(f"    reinterpret_cast<const void*>(&JitBranchRelocTarget_{i}),\n")
            f.write("};\n\n")
            f.write("const char* const branch_reloc_target_names[] = {\n")
            for symbol in branch_targets:
                escaped = symbol.replace("\\", "\\\\").replace("\"", "\\\"")
                f.write(f"    \"{escaped}\",\n")
            f.write("};\n\n")
        else:
            f.write("const void* const* branch_reloc_targets = nullptr;\n\n")
            f.write("const char* const* branch_reloc_target_names = nullptr;\n\n")
        if addr_targets:
            for i, target in enumerate(addr_targets):
                if target["storage"] != "embedded":
                    continue
                align = max(1, int(target.get("align", 1)))
                bytes_list = ", ".join(f"0x{b:02x}" for b in target["bytes"])
                f.write(f"alignas({align}) const uint8_t addr_reloc_blob_{i}[] = {{ {bytes_list} }};\n")
            f.write("const void* const addr_reloc_targets[] = {\n")
            for i, target in enumerate(addr_targets):
                if target["storage"] == "embedded":
                    f.write(f"    addr_reloc_blob_{i},\n")
                else:
                    f.write(f"    reinterpret_cast<const void*>(&JitAddrRelocTarget_{i}),\n")
            f.write("};\n\n")
            f.write("const char* const addr_reloc_target_names[] = {\n")
            for target in addr_targets:
                escaped = target["name"].replace("\\", "\\\\").replace("\"", "\\\"")
                f.write(f"    \"{escaped}\",\n")
            f.write("};\n\n")
        else:
            f.write("const void* const* addr_reloc_targets = nullptr;\n\n")
            f.write("const char* const* addr_reloc_target_names = nullptr;\n\n")
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
            if data.get('addr_relocs'):
                f.write(f"const AddrRelocDesc stencil_addr_relocs_{name}[] = {{\n")
                for r in data['addr_relocs']:
                    f.write(
                        f"    {{ {r['page21_offset']}, {r['pageoff12_offset']}, {r['target_id']}, "
                        f"AddrRelocKind::{r['kind']}, {r['shift']} }},\n"
                    )
                f.write("};\n")
            else:
                f.write(f"const AddrRelocDesc* stencil_addr_relocs_{name} = nullptr;\n")
            
        f.write("\nconst StencilDesc stencils[] = {\n")
        for i, name in enumerate(sorted(stencils.keys())):
            data = stencils[name]
            p_ptr = f"stencil_patches_{name}" if data['patches'] else "nullptr"
            r_ptr = f"stencil_branch_relocs_{name}" if data['branch_relocs'] else "nullptr"
            a_ptr = f"stencil_addr_relocs_{name}" if data.get('addr_relocs') else "nullptr"
            f.write(
                f"    {{ stencil_bytes_{name}, sizeof(stencil_bytes_{name}), {p_ptr}, {len(data['patches'])}, "
                f"{r_ptr}, {len(data['branch_relocs'])}, {a_ptr}, {len(data.get('addr_relocs', []))}, {i}, 0 }}, // {name}\n"
            )
        f.write("};\n\nstruct HandlerStencilMapEntry { ::fiberish::HandlerFunc target; uint16_t stencil_id; };\n")
        f.write("const char* const stencil_names[] = {\n")
        for name in sorted(stencils.keys()):
            f.write(f"    \"{name}\",\n")
        f.write("};\n")
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

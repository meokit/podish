#!/usr/bin/env python3
"""Rank executable Wasm handler bindings from several V8 CPU profiles."""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess


FRAME = re.compile(r"wasm-function\[(\d+)\]")
FUNCTION = re.compile(r" func\[(\d+)\]:$")
SLOT = re.compile(r"elem\[(\d+)\] = ref.func:(\d+)")


def leb(blob, offset, signed=False):
    value = shift = 0
    while True:
        part = blob[offset]
        offset += 1
        value |= (part & 127) << shift
        shift += 7
        if part < 128:
            if signed and part & 64:
                value -= 1 << shift
            return value, offset


def initial_memory(blob):
    """Read the active memory-0 segments of an Emscripten browser module."""
    pos = 8
    while pos < len(blob):
        section = blob[pos]
        size, start = leb(blob, pos + 1)
        pos = start + size
        if section == 11:
            break
    else:
        raise ValueError("No Wasm data section")
    count, pos = leb(blob, start)
    memory = bytearray()
    for _ in range(count):
        mode, pos = leb(blob, pos)
        dest = None
        if mode in (0, 2):
            if mode == 2:
                index, pos = leb(blob, pos)
                if index:
                    raise ValueError("Expected the default Wasm memory")
            if blob[pos] != 0x41:
                raise ValueError("Expected a constant data offset")
            dest, pos = leb(blob, pos + 1, signed=True)
            if dest < 0 or blob[pos] != 0x0b:
                raise ValueError("Unsupported data offset")
            pos += 1
        elif mode != 1:
            raise ValueError("Unsupported Wasm data segment")
        size, start = leb(blob, pos)
        pos = start + size
        if dest is not None and size:
            if len(memory) < dest + size:
                memory.extend(b"\0" * (dest + size - len(memory)))
            memory[dest:dest + size] = blob[start:pos]
    return memory


def reflection_layout(wasm, symbols):
    """Read the handler count and the base of {slot, name} from the getters."""
    wanted = {symbols[name]: name for name in ("X86_GetHandlerCount", "X86_GetHandlerById")}
    bodies = {}
    with subprocess.Popen(["wasm-objdump", "-d", wasm], stdout=subprocess.PIPE, text=True) as proc:
        current = None
        for line in proc.stdout:
            if header := FUNCTION.search(line):
                if len(bodies) == len(wanted):
                    proc.terminate()
                    break
                current = wanted.get(int(header[1]))
                if current:
                    bodies[current] = []
            elif current and "|" in line:
                bodies[current].append(line.split("|", 1)[1].strip())
    (count,) = re.findall(r"^i32.const (\d+)$", "\n".join(bodies["X86_GetHandlerCount"]), re.M)
    (base,) = re.findall(r"i32.const (\d+)\ni32.add\ni32.load 2 0", "\n".join(bodies["X86_GetHandlerById"]))
    return int(count), int(base)


def binding(name):
    if match := re.match(r"fiberish::(OperandClone_\w+)\(", name):
        return (match[1],)
    if "DispatchWrapper<&" not in name:
        return None
    arguments = name.split("DispatchWrapper<&", 1)[1]
    policy_match = re.search(r"\), (fiberish::OperandSpec::.*)>\(fiberish::EmuState\*", arguments)
    if not policy_match:
        return None
    target = arguments.split("(fiberish::EmuState*", 1)[0].removeprefix("fiberish::LogicFlow ")
    target = target.replace("fiberish::", "")
    policy = policy_match[1].replace("fiberish::", "").replace("(unsigned char)", "")
    if target.startswith("baked_operand::"):
        target = target.split("<", 1)[0]
    return (target, policy)


def function_bindings(wasm, blob, symbols):
    count, base = reflection_layout(wasm, symbols)
    memory = initial_memory(blob)
    element = subprocess.check_output(["wasm-objdump", "-x", "-j", "Elem", wasm], text=True)
    functions = {int(slot): int(func) for slot, func in SLOT.findall(element)}
    groups = defaultdict(list)
    for index in range(count):
        slot, pointer = struct.unpack_from("<II", memory, base + 8 * index)
        if slot in functions:
            name = memory[pointer:memory.index(0, pointer)].decode()
            if row := binding(name):
                groups[functions[slot]].append(row)
    return {index: tuple(dict.fromkeys(rows)) for index, rows in groups.items()}


def sampled_time(profile, run_index):
    frames = {
        node["id"]: (node["callFrame"]["url"], int(match[1]))
        for node in profile["nodes"] if (match := FRAME.fullmatch(node["callFrame"]["functionName"]))
    }
    (module,) = {url for url, index in frames.values() if index == run_index}
    time = Counter()
    for node, elapsed in zip(profile["samples"], profile["timeDeltas"], strict=True):
        if node in frames and frames[node][0] == module:
            time[frames[node][1]] += elapsed
    return time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wasm", type=Path, required=True)
    parser.add_argument("--symbols", type=Path, required=True)
    parser.add_argument("--profile", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    blob = args.wasm.read_bytes()
    digest = hashlib.sha256(blob).hexdigest()
    symbols = {name: int(index) for index, name in
               (line.split(":", 1) for line in args.symbols.read_text().splitlines())}
    functions = function_bindings(args.wasm, blob, symbols)
    ranking = Counter()
    for path in args.profile:
        time = sampled_time(json.loads(path.read_text()), symbols["X86_Run"])
        eligible = {index: elapsed for index, elapsed in time.items() if index in functions}
        total = sum(eligible.values())
        if not total:
            raise ValueError(f"No eligible handler samples in {path}")
        ranking.update({index: elapsed / total for index, elapsed in eligible.items()})
        print(f"{path.name}: {len(eligible)} bodies, eligible time {total / 1e6:.2f}s")

    ranked = sorted(ranking, key=lambda index: (-ranking[index], index))
    plan = []
    seen = set()
    for index in ranked:
        group = [row for row in functions[index] if row not in seen]
        if group:
            seen.update(group)
            plan.append(group)
    output = {
        "workload": ", ".join(path.stem for path in args.profile),
        "wasmSha256": digest,
        "handlers": plan,
    }
    args.output.write_text(json.dumps(output, indent=2) + "\n")
    print(f"{len(plan)} ranked bodies, {len(seen)} distinct handler bindings")


if __name__ == "__main__":
    main()

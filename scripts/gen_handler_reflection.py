#!/usr/bin/env python3

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

TEMPLATE_NAME = "fiberish::MemoryOpRestart"

HANDLER_PROTO = (
    'fiberish::EmuState* RESTRICT state, '
    'fiberish::DecodedOp* RESTRICT op, '
    'int64_t instr_limit, '
    'fiberish::mem::MicroTlbAbiWord utlb_tags, '
    'fiberish::mem::MicroTlbAbiWord utlb_addend, '
    'uint32_t branch, '
    'uint64_t flags_cache'
)


def find_tool(*names: str) -> str | None:
    for name in names:
        resolved = shutil.which(name)
        if resolved:
            return resolved

    compiler_candidates = [
        os.environ.get("CXX"),
        os.environ.get("CC"),
        shutil.which("clang-cl"),
        shutil.which("clang++"),
        shutil.which("clang"),
    ]
    for compiler in compiler_candidates:
        if not compiler:
            continue
        compiler_path = Path(compiler)
        for name in names:
            candidate = compiler_path.with_name(name)
            if os.name == "nt" and candidate.suffix.lower() != ".exe":
                candidate = candidate.with_suffix(".exe")
            if candidate.exists():
                return str(candidate)

    return None


LLVM_NM = find_tool("llvm-nm")
NM_TOOL = LLVM_NM or find_tool("nm")
if NM_TOOL is None:
    raise RuntimeError("failed to locate nm or llvm-nm for handler reflection generation")


def run_tool(command: list[str]) -> list[str]:
    proc = subprocess.run(
        command,
        text=True,
        capture_output=True,
        check=True,
    )
    return [line.rstrip("\r") for line in proc.stdout.splitlines() if line.strip()]


def run_nm(obj_path: Path) -> list[tuple[str, str]]:
    if Path(NM_TOOL).name.lower().startswith("llvm-nm"):
        mangled_names = run_tool([NM_TOOL, "--just-symbol-name", "--defined-only", "--extern-only", str(obj_path)])
        demangled_names = run_tool(
            [NM_TOOL, "--just-symbol-name", "--defined-only", "--extern-only", "--demangle", str(obj_path)]
        )
        if len(mangled_names) != len(demangled_names):
            raise RuntimeError(f"llvm-nm returned mismatched symbol counts for {obj_path}")
        return list(zip(mangled_names, demangled_names))

    proc = subprocess.run(
        [NM_TOOL, "-g", "-P", str(obj_path)],
        text=True,
        capture_output=True,
        check=True,
    )
    entries: list[tuple[str, str]] = []
    for line in proc.stdout.splitlines():
        parts = line.strip().split()
        if len(parts) < 2:
            continue
        name, sym_type = parts[0], parts[1]
        if sym_type not in {"T", "W"}:
            continue
        entries.append((name, name))
    return entries


def demangle_names(names: list[str]) -> dict[str, str]:
    if not names:
        return {}

    filt_input = []
    for name in names:
        filt_input.append(name[1:] if name.startswith("__Z") else name)

    proc = subprocess.run(
        ["c++filt", "-n"],
        input="\n".join(filt_input) + "\n",
        text=True,
        capture_output=True,
        check=True,
    )
    lines = proc.stdout.splitlines()
    if len(lines) != len(names):
        raise RuntimeError("c++filt returned unexpected line count")
    return dict(zip(names, lines))


def extract_signature_suffix(name: str, function_name: str | None = None) -> str | None:
    anchor = 0
    if function_name is not None:
        anchor = name.find(function_name)
        if anchor < 0:
            return None
        anchor += len(function_name)

    paren_index = name.find("(", anchor)
    if paren_index < 0:
        return None
    return name[paren_index:]


def escape_cpp_string(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate fibercpu handler reflection table")
    parser.add_argument("--output", required=True)
    parser.add_argument("objects", nargs="+")
    args = parser.parse_args()

    object_paths = [Path(obj) for obj in args.objects]
    seen: set[str] = set()
    mangled_names: list[str] = []
    demangled: dict[str, str] = {}
    for obj_path in object_paths:
        for name, maybe_demangled_name in run_nm(obj_path):
            if name in seen:
                continue
            seen.add(name)
            mangled_names.append(name)
            demangled[name] = maybe_demangled_name

    if not Path(NM_TOOL).name.lower().startswith("llvm-nm"):
        demangled = demangle_names(mangled_names)

    template_signature = None
    for mangled_name in mangled_names:
        name = demangled.get(mangled_name, "")
        template_signature = extract_signature_suffix(name, TEMPLATE_NAME)
        if template_signature is not None:
            break

    if template_signature is None:
        raise RuntimeError(f"failed to locate template handler ABI from {TEMPLATE_NAME}")

    matches: list[tuple[str, str]] = []
    for mangled_name in mangled_names:
        name = demangled.get(mangled_name, "")
        if name.endswith(template_signature):
            matches.append((mangled_name, name))

    matches.sort(key=lambda item: item[0])

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    with output.open("w", encoding="utf-8") as f:
        f.write("// Auto-generated by gen_handler_reflection.py\n")
        f.write("#include <cstddef>\n")
        f.write("#include <cstdint>\n")
        f.write('#include "decoder.h"\n\n')
        f.write("#if !defined(__clang__) && !defined(__GNUC__)\n")
        f.write('#error "handler reflection generation currently requires Clang or GCC asm labels"\n')
        f.write("#endif\n\n")

        for index, (mangled_name, _demangled_name) in enumerate(matches):
            f.write(
                f'extern "C" ATTR_PRESERVE_NONE int64_t fibercpu_handler_reflect_{index}({HANDLER_PROTO}) '
                f'__asm__("{escape_cpp_string(mangled_name)}");\n'
            )

        f.write("\nnamespace fiberish::generated::handler_reflection {\n\n")
        f.write("struct HandlerReflectionEntry {\n")
        f.write("    HandlerFunc handler;\n")
        f.write("    const char* name;\n")
        f.write("};\n\n")
        f.write("static const HandlerReflectionEntry kEntries[] = {\n")
        for index, (_mangled_name, demangled_name) in enumerate(matches):
            f.write(
                f'    {{fibercpu_handler_reflect_{index}, "{escape_cpp_string(demangled_name)}"}},\n'
            )
        f.write("};\n\n")
        f.write("constexpr size_t kEntryCount = sizeof(kEntries) / sizeof(kEntries[0]);\n\n")
        f.write("size_t HandlerCount() { return kEntryCount; }\n\n")
        f.write("HandlerFunc HandlerForId(uint32_t id) {\n")
        f.write("    return id < kEntryCount ? kEntries[id].handler : nullptr;\n")
        f.write("}\n\n")
        f.write("int32_t IdForHandler(HandlerFunc handler) {\n")
        f.write("    if (handler == nullptr) return -1;\n")
        f.write("    for (uint32_t i = 0; i < kEntryCount; ++i) {\n")
        f.write("        if (kEntries[i].handler == handler) return static_cast<int32_t>(i);\n")
        f.write("    }\n")
        f.write("    return -1;\n")
        f.write("}\n\n")
        f.write("const char* NameForId(uint32_t id) {\n")
        f.write("    return id < kEntryCount ? kEntries[id].name : nullptr;\n")
        f.write("}\n\n")
        f.write("}  // namespace fiberish::generated::handler_reflection\n")

    print(f"Generated {output} with {len(matches)} handler entries using ABI {template_signature}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

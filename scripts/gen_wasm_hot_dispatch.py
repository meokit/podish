#!/usr/bin/env python3
"""Generate the inlined Wasm dispatch cases for a ranked handler plan."""

import argparse
import json
import re
from pathlib import Path


SCALAR_KERNEL = re.compile(r"^FORCE_INLINE LogicFlow (\w+)\(LogicFuncParams\) \{\n[^}]*^\}", re.M)
FUSION_HANDLER = re.compile(r"^ATTR_PRESERVE_NONE int64_t (OperandClone_\w+)\([^\{]*\{\n(.*?)^\}", re.M | re.S)
OPERAND_SHAPE = re.compile(r"DecodedOperandShape\{([\w, ]+)\}")
FUSION_TAIL = re.compile(
    r"    if \(auto\* next = ([^\n]+)\) \{\n"
    r"        mem::EncodeMicroTlbAbi\(utlb, utlb_tags, utlb_addend\);\n"
    r"        ATTR_MUSTTAIL return next->handler\([^\n]+\);\n"
    r"    \}\n    __builtin_unreachable\(\);\n\Z"
)


def fusion_case(body):
    body, count = FUSION_TAIL.subn(
        r"    locals.op = \1;\n"
        r"    mem::EncodeMicroTlbAbi(utlb, locals.tags, locals.addend);\n"
        r"    locals.branch = branch;\n"
        r"    locals.flags = flags_cache;\n"
        r"    continue;\n",
        body,
    )
    if count != 1 or "ATTR_MUSTTAIL" in body:
        raise ValueError("unexpected tail dispatch in generated fusion")
    body = body.replace("    FIBERCPU_PIN_ASSUME;\n", "")
    body = body.replace("DecodeMicroTlbAbi(utlb_tags, utlb_addend)", "DecodeMicroTlbAbi(locals.tags, locals.addend)")
    return body.replace("RUN_SUPEROPCODE_OP_WITH_POLICY(", "HOT_FUSION_OP(")


def complete_operand_shape(match):
    # The demangler omits trailing zero members of a structural template argument.
    fields = match[1].split(", ")
    return "DecodedOperandShape{" + ", ".join(fields + ["0u"] * (4 - len(fields))) + "}"


def generate(plan, budget, scalar_sources, fusion_source):
    kernels = {
        match[1]: match[0] for source in scalar_sources for match in SCALAR_KERNEL.finditer(source)
    }
    fusions = dict(FUSION_HANDLER.findall(fusion_source))
    headers = ['#include "baked_operand_kernels.h"']
    op_headers = Path(__file__).resolve().parents[1] / "libfibercpu" / "src" / "ops"
    headers[:0] = [f'#include "ops/{path.name}"' for path in sorted(op_headers.glob("*_impl.h"))]

    declarations = []
    required_kernels = set()
    handlers = ["g_ExitHandlersFallthrough[0]", "g_ExitHandlersBranch[0]", "ForwardMemoryImmediateSlot"]
    cases = [
        "            case 1:",
        "                locals.branch = state->last_block->sentinel_eip;",
        "                [[fallthrough]];",
        "            case 2:",
        "                RecordBlockHandlersUntil(state, locals.op);",
        "                goto follow_single_link;",
        "            case 3:",
        "                ++locals.op;",
        "                continue;",
    ]
    # Binaryen can merge several C++ bindings into one sampled Wasm body.
    # Include all of its table-slot identities, with each binding's own policy.
    bindings = dict.fromkeys(tuple(row) for group in plan["handlers"][:budget] for row in group)
    for row in bindings:
        if len(row) == 1:
            name, = row
            if name not in fusions:
                continue
            declarations.append(f"ATTR_PRESERVE_NONE int64_t {name}(EmuState*, DecodedOp*, int64_t, "
                                "mem::MicroTlbAbiWord, mem::MicroTlbAbiWord, uint32_t, uint64_t);")
            cases.append(f"            case {len(handlers) + 1}: {{")
            cases.extend([
                "                auto* op = locals.op;",
                "                auto branch = locals.branch;",
                "                auto flags_cache = locals.flags;",
            ])
            cases.extend("            " + line if line else "" for line in fusion_case(fusions[name]).splitlines())
            cases.append("            }")
            handlers.append(name)
            continue
        target, policy = row
        policy = OPERAND_SHAPE.sub(complete_operand_shape, policy)
        if target.startswith("baked_operand::"):
            target += f"<{policy}>"
        elif not target.startswith("op::"):
            if target not in kernels:
                continue
            required_kernels.add(target)
        binding = f"<{target}, {policy}>"
        cases.extend([
            f"            case {len(handlers) + 1}:",
            f"                flow = RunHotScalar{binding}(state, locals);",
            "                if (flow == LogicFlow::Continue) [[likely]] continue;",
            "                break;",
        ])
        handlers.append(f"DispatchWrapper{binding}")

    kernel_lines = [
        "// Generated Wasm hot dispatch kernels and handler table.",
        *headers,
        "namespace fiberish {",
        *(kernels[name] for name in sorted(required_kernels)),
        *declarations,
        "namespace {",
        "const HandlerFunc kHotHandlers[] = {",
        "    nullptr,",
        *(f"    {handler}," for handler in handlers),
        "};",
        "}  // namespace",
        "}",
    ]
    return kernel_lines, ["// Generated Wasm hot dispatch cases.", *cases], len(handlers)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--budget", type=int, required=True)
    parser.add_argument("--scalar", type=Path, nargs="+", required=True)
    parser.add_argument("--fusions", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    plan = json.loads(args.plan.read_text())
    if not 0 < args.budget <= len(plan["handlers"]):
        parser.error(f"handler budget must be between 1 and {len(plan['handlers'])}")
    kernels, cases, count = generate(plan, args.budget, (path.read_text() for path in args.scalar),
                                     args.fusions.read_text() if args.fusions else "")
    for name, lines in (("wasm_hot_kernels.inc", kernels), ("wasm_hot_cases.inc", cases)):
        output = args.output / name
        content = "\n".join(lines) + "\n"
        if not output.exists() or output.read_text() != content:
            output.write_text(content)
    print(f"Generated {count} Wasm hot cases ({count - 3} handler bindings and 3 dispatch helpers)")


if __name__ == "__main__":
    main()

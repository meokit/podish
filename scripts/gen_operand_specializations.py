#!/usr/bin/env python3
"""Bake bounded decoded operands and register-class policies."""
import argparse, json, pathlib, re

from operand_specialization_data import FUSION_CALL, Operand, generated_fusions, instruction_targets

p = argparse.ArgumentParser()
p.add_argument('--source', required=True)
p.add_argument('--plan', required=True)
p.add_argument('--families', required=True)
p.add_argument('--variants', required=True)
p.add_argument('--fallbacks', required=True)
p.add_argument('--output', required=True)
p.add_argument('--mode', type=int, default=0)
p.add_argument('--budget', type=int, default=256)
p.add_argument('--pin-specialization', type=int, default=0)
p.add_argument('--fused-only', type=int, default=0)
p.add_argument('--pin-mode', type=int, default=0)
p.add_argument('--pin-mask', type=int, default=0)
a = p.parse_args()
if not 0 <= a.budget <= 1024:
    p.error("budget must be between 0 and the bounded 1024-shape limit")
if a.mode not in range(4) or a.pin_mode not in range(5) or a.pin_specialization not in range(3):
    p.error("invalid lowering or pin mode")
if not 0 <= a.pin_mask <= 0xff or bool(a.pin_mode) != bool(a.pin_mask):
    p.error("pin mask must describe the enabled guest GPRs")
pin_bits = {reg: 1 << slot for slot, reg in enumerate(reg for reg in range(8) if a.pin_mask & (1 << reg))}
all_pins = (1 << len(pin_bits)) - 1
basic_pin = bool(pin_bits) and tuple(pin_bits) != (0, 4)
source = pathlib.Path(a.source).read_text()
targets = instruction_targets(pathlib.Path(a.families), pathlib.Path(a.variants), pathlib.Path(a.fallbacks))
plan = json.loads(pathlib.Path(a.plan).read_text())
full_profile = isinstance(plan, dict)
if full_profile:
    if type(plan.get("version")) is not int or plan["version"] != 2 or not isinstance(plan.get("entries"), list):
        p.error("expected a version 2 decoded operand plan")
    plan = plan["entries"]
elif not isinstance(plan, list):
    p.error("expected a legacy operand shape list or version 2 decoded operand plan")
def traits(op):
    # On unverified pin sets restrict new clones to basic ModRM ALU, MOV,
    # CMP and TEST. The ordinary ISA specialization table still applies.
    if basic_pin and not ((op < 0x3c and op % 8 < 4) or op in
                            (0x84, 0x85, 0x88, 0x89, 0x8a, 0x8b, 0x200, 0x202)):
        return None
    if a.pin_specialization:
        if 0x40 <= op <= 0x4f or 0xb8 <= op <= 0xbf or 0x1c8 <= op <= 0x1cf: return ('Rm', False, 'Opcode')
        if 0xb0 <= op <= 0xb7: return ('Rm', True, 'Opcode')
        if op == 0x8d: return ('Reg', False, 'Address')
        if 0x70 <= op <= 0x7f or 0x180 <= op <= 0x18f: return ('None', False, 'Opcode')
    if a.pin_specialization >= 2 and op in (0x201, 0x203):
        return ('Rm' if op == 0x201 else 'Reg', False, 'Memory')
    if op in (0x200, 0x202): return ('Rm' if op == 0x200 else 'Reg', False, False)
    if op < 0x3c and op % 8 < 4:
        return ('None' if op >= 0x38 else ('Rm' if op % 8 < 2 else 'Reg'), op % 2 == 0, False)
    if op in (0x84, 0x85): return ('None', op == 0x84, False)
    if op in (0x88, 0x89, 0x8a, 0x8b): return ('Rm' if op < 0x8a else 'Reg', op in (0x88, 0x8a), False)
    if op in (0x80, 0x81, 0x82, 0x83): return ('Rm', op in (0x80, 0x82), True)
    if op in (0xc0, 0xc1, 0xd0, 0xd1, 0xd2, 0xd3): return ('Rm', op in (0xc0, 0xd0, 0xd2), False)
    if op in (0x1af, 0x1b6, 0x1b7, 0x1be, 0x1bf): return ('Reg', False, False)
    return None
safe = {}
for name, codes in targets.items():
    choices = {traits(c) for c in codes}
    if len(choices) == 1 and None not in choices: safe[name] = choices.pop()
def keep_for(name, shape):
    t = safe.get(name)
    if t is None: return 0
    role, byte, group = t
    if group not in ('Opcode','Address') and (shape & 0xc0) != 0xc0:
        # These integer kernels use nonblocking MMU access. A miss exits via
        # Restart/Retry (which reload pins); normal completion has no observer.
        # Guest memory is separate from EmuState. A memory Rm destination cannot
        # write a guest GPR. Keep this opt-in and restricted to audited families.
        codes = targets[name]
        memory_safe = all((op < 0x3c and op % 8 < 4) or op in
                          (0x201, 0x203, 0x84, 0x85, 0x88, 0x89, 0x8a, 0x8b,
                           0x1b6, 0x1b7, 0x1be, 0x1bf) for op in codes)
        if a.pin_specialization < 2 or not memory_safe: return 0
        if role == 'Rm': return all_pins
    if role == 'None' or group is True and ((shape >> 3) & 7) == 7: return all_pins
    field = (shape >> 3) if role == 'Reg' else shape
    idx = field & (3 if byte else 7)
    return all_pins & ~pin_bits.get(idx, 0)

code = ['// Generated decoded operand lowering.', '#include "operand_specialization.h"']
if full_profile:
    # Custom fusion source may include only the kernels used in that table.
    headers = pathlib.Path(a.fallbacks).parent / "ops"
    code.extend(f'#include "ops/{header.name}"' for header in sorted(headers.glob("*_impl.h")))
code.append('namespace fiberish {')
fusions = generated_fusions(source)
entries = []
skipped = 0
if a.mode in (1, 3) and (full_profile or not basic_pin):
    for i, item in enumerate(plan if full_profile else plan[:a.budget]):
        if len(entries) >= a.budget:
            break
        if a.fused_only and item['kind'] != 'fused': continue
        original = item['original']; width = item['width']
        if type(width) is not int or width not in range(1,5):
            raise ValueError('Invalid operand plan width: ' + original)
        if item['kind'] not in ('wrapper', 'fused') or item['kind'] == 'wrapper' and width != 1:
            raise ValueError('Invalid operand plan kind/width: ' + original)
        if full_profile:
            operands = [Operand(**fields) for fields in item['operands']]
            if len(operands) != width:
                raise ValueError('Invalid decoded operand count: ' + original)
            shapes = [op.legacy_shape() for op in operands]
            cpp_shapes = [op.cpp() for op in operands]
        else:
            shapes = item['shapes']
            if len(shapes) != 4 or any(type(v) is not int or not 0 <= v < 2**48 for v in shapes):
                raise ValueError('Invalid operand plan entry: ' + original)
            cpp_shapes = [f'{shape}ULL' for shape in shapes]
        if item['kind'] == 'wrapper':
            match = re.fullmatch(r'DispatchWrapper<op::(\w+)>', original)
            if not match or match[1] not in targets:
                skipped += 1
                continue
            name = match[1]
            policy = 'ProfileOperandPolicy' if full_profile else 'ExactOperandPolicy'
            target = f'DispatchWrapper<op::{name}, {policy}<{cpp_shapes[0]}, {keep_for(name, shapes[0])}>>'
        else:
            body = fusions.get(original)
            if body is None:
                skipped += 1
                continue
            calls = list(FUSION_CALL.finditer(body))
            if len(calls) != width:
                raise ValueError('Fusion width does not match selected source: ' + original)
            clone = f'OperandClone_{i:03d}_{original}'
            if full_profile:
                for call, operand in reversed(list(zip(calls, operands))):
                    replacement = 'RUN_SUPEROPCODE_OP_IMM' + call[0][len('RUN_SUPEROPCODE_OP'):-1]
                    body = body[:call.start()] + replacement + f', 0x{operand.imm:08x}u)' + body[call.end():]
                assumptions = [f'    ProfileOperandPolicy<{shape}>::Assume(op + {j});\n'
                               for j, shape in enumerate(cpp_shapes)]
            else:
                assumptions = [f'    AssumeOperandShape<{cpp_shapes[j]}>(op + {j});\n' for j in range(width)]
            pos = body.index('{') + 1
            body = body[:pos] + '\n' + ''.join(assumptions) + body[pos:]
            body = body.replace(original, clone)
            keep = all_pins
            for call, shape in zip(calls, shapes):
                keep &= keep_for(call[1], shape)
            # Exits remain conservative; only the final normal tail may preserve known nonwrites.
            key = body.rfind('flags_cache FIBERCPU_PIN_RELOAD')
            if key >= 0:
                alias = f'OperandKeep_{i:03d}'
                code.append(f'using {alias} = ExactOperandPolicy<0, {keep}>;')
                body = body[:key] + body[key:].replace('flags_cache FIBERCPU_PIN_RELOAD',f'flags_cache FIBERCPU_PIN_FORWARD({alias})',1)
            code.append(body); target = clone
        vals = ','.join(cpp_shapes)
        entries.append(f'    {{{original}, {target}, {{{vals}}}, {width}}},')
code += ['namespace {']
shape_entry = 'ProfileOperandShapeEntry' if full_profile else 'OperandShapeEntry'
shape_template = ', DecodedOperandShape' if full_profile else ''
code += [f'static const {shape_entry} shapes[] = {{'] + (entries or ['    {},']) + ['};',
         f'constexpr size_t shape_count = {len(entries)};']
registers = []
if not a.fused_only and (a.mode in (2, 3) or a.pin_mode >= 3):
    for name, (role, byte, group) in sorted(safe.items()):
        if group == 'Memory': continue  # Exact shapes only; never claim a register form.
        if group in ('Opcode', 'Address'):
            registers.append(f'    {{DispatchWrapper<op::{name}>, SelectEncodedOperand<op::{name}, PinWriteRole::{role}, {str(byte).lower()}, {str(group == "Opcode").lower()}>, PinOperandForm::{group}}},')
        else:
            registers.append(f'    {{DispatchWrapper<op::{name}>, SelectRegisterOperand<op::{name}, PinWriteRole::{role}, {str(byte).lower()}, {str(group).lower()}>}},')
code += ['static const RegisterOperandEntry registers[] = {'] + (registers or ['    {},']) + ['};',
         f'constexpr size_t register_count = {len(registers)};',
         'const auto& OperandLookup() {',
         f'    static const OperandIndex<(register_count != 0), {str(bool(a.pin_specialization and not a.fused_only)).lower()}{shape_template}> index(shapes, shape_count, registers, register_count);',
         '    return index;', '}', '} // namespace',
         '''HandlerFunc CanonicalOperandHandler(HandlerFunc handler) {
    static const auto originals = [] {
        ankerl::unordered_dense::map<HandlerFunc, HandlerFunc> result;
        for (size_t i=0; i<shape_count; ++i) result[shapes[i].replacement] = shapes[i].original;
        DecodedOp dummy{};
        for (size_t i=0; i<register_count; ++i) {
            for (unsigned modrm=0xc0; modrm<=0xff; ++modrm) {
                dummy.modrm = modrm;
                result[registers[i].select(&dummy)] = registers[i].original;
            }
        }
        return result;
    }();
    const auto found = originals.find(handler);
    return found == originals.end() ? handler : found->second;
}''', 'HandlerFunc LowerOperandHandler(const DecodedOp* op) {',
         '    return OperandLookup().Find(op);',
          '}',
         'HandlerFunc LowerFusionOperandHandler(const DecodedOp* op, HandlerFunc handler) {',
         '    return OperandLookup().Find(op, handler);', '}',
         'bool AcceptFusionOperandShape(const DecodedOp* op, HandlerFunc handler) {',
         '    return OperandLookup().Find(op, handler) != nullptr;',
         '}', '} // namespace fiberish']
path = pathlib.Path(a.output); path.parent.mkdir(parents=True, exist_ok=True); path.write_text('\n'.join(code) + '\n')
print(f'Operand lowering: {len(entries)} exact shapes, {len(registers)} register families')
if full_profile and skipped:
    print(f'Skipped {skipped} operand entries absent from the selected instruction or fusion source')

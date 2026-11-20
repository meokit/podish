#!/usr/bin/env python3
"""Bake bounded decoded operands and register-class policies."""
import argparse, json, pathlib, re

from gen_instruction_catalog import read_catalog
from operand_specialization_data import (
    FUSION_CALL, Operand, generated_fusions, instruction_targets, resolve_training_plan, specialize_fusion,
    training_plan_match,
)

p = argparse.ArgumentParser()
p.add_argument('--source', required=True)
p.add_argument('--plan', required=True)
p.add_argument('--catalog', required=True, type=pathlib.Path)
p.add_argument('--output', required=True)
p.add_argument('--compact-immediates', required=True, type=pathlib.Path)
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
catalog = read_catalog(a.catalog)
targets = instruction_targets(catalog)
plan = json.loads(pathlib.Path(a.plan).read_text())
fusions = generated_fusions(source)
try:
    decoded_match = training_plan_match(plan) == "decoded"
    plan = resolve_training_plan(plan, catalog, fusions, None if decoded_match else a.budget)
except ValueError as error:
    p.error(str(error))
pin_rules = {opcode: row for row in catalog["pin_rules"] for opcode in row["opcodes"]}

def traits(opcode):
    rule = pin_rules.get(opcode)
    if not rule or (basic_pin and not rule["basic"]) or a.pin_specialization < rule["minimum_pin_mode"]:
        return None
    return rule["role"], rule["byte"], rule["form"]

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
        memory_safe = all(pin_rules[opcode]["memory"] for opcode in targets[name])
        if a.pin_specialization < 2 or not memory_safe: return 0
        if role == 'Rm': return all_pins
    if role == 'None' or group == 'Group' and ((shape >> 3) & 7) == 7: return all_pins
    field = (shape >> 3) if role == 'Reg' else shape
    idx = field & (3 if byte else 7)
    return all_pins & ~pin_bits.get(idx, 0)

code = ['// Generated decoded operand lowering.', '#include "operand_specialization.h"']
compact = {}
if not a.fused_only and a.mode in (1, 3):
    for row in json.loads(a.compact_immediates.read_text()):
        values = row["immediates"]
        if not values or any(type(value) is not int or not 0 < value <= 0xffffffff for value in values):
            p.error("expected nonzero uint32 compact immediates")
        if len(set(values)) != len(values):
            p.error("duplicate compact immediate in selector")
        for name in row["handlers"]:
            if name not in targets or name in compact:
                p.error(f"unknown or duplicate compact-immediate handler: {name}")
            compact[name] = values

if decoded_match:
    # Custom fusion source may include only the kernels used in that table.
    headers = a.catalog.parent / "ops"
    code.extend(f'#include "ops/{header.name}"' for header in sorted(headers.glob("*_impl.h")))
elif compact:
    code.extend(['#include "ops/ops_data_mov_impl.h"', '#include "ops/ops_groups_impl.h"'])
code.append('namespace fiberish {')
entries = []
skipped = 0
if a.mode in (1, 3) and (decoded_match or not basic_pin):
    for i, item in enumerate(plan):
        if len(entries) >= a.budget:
            break
        if a.fused_only and item['kind'] != 'fused': continue
        original = item['original']; width = item['width']
        if type(width) is not int or width not in range(1,5):
            raise ValueError('Invalid operand plan width: ' + original)
        if item['kind'] not in ('wrapper', 'fused') or item['kind'] == 'wrapper' and width != 1:
            raise ValueError('Invalid operand plan kind/width: ' + original)
        if decoded_match:
            operands = [Operand(**fields) for fields in item['operands']]
            if len(operands) != width:
                raise ValueError('Invalid decoded operand count: ' + original)
            shapes = [op.shape_key() for op in operands]
            cpp_shapes = [op.cpp() for op in operands]
        else:
            shapes = item['shapeKeys']
            if len(shapes) != width or any(type(v) is not int or not 0 <= v < 2**48 for v in shapes):
                raise ValueError('Invalid operand plan entry: ' + original)
            cpp_shapes = [f'{shape}ULL' for shape in shapes] + ['0ULL'] * (4 - width)
        if item['kind'] == 'wrapper':
            match = re.fullmatch(r'DispatchWrapper<op::(\w+)>', original)
            if not match or match[1] not in targets:
                skipped += 1
                continue
            name = match[1]
            policy = 'OperandSpec::Profile' if decoded_match else 'OperandSpec::Exact'
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
            policy = 'OperandSpec::Profile' if decoded_match else 'OperandSpec::Exact'
            body = specialize_fusion(body, original, clone, [f'{policy}<{shape}>' for shape in cpp_shapes[:width]])
            keep = all_pins
            for call, shape in zip(calls, shapes):
                keep &= keep_for(call[1], shape)
            # Exits remain conservative; only the final normal tail may preserve known nonwrites.
            key = body.rfind('flags_cache FIBERCPU_PIN_RELOAD')
            if key >= 0:
                alias = f'OperandKeep_{i:03d}'
                code.append(f'using {alias} = OperandSpec::Exact<0, {keep}>;')
                body = body[:key] + body[key:].replace('flags_cache FIBERCPU_PIN_RELOAD',f'flags_cache FIBERCPU_PIN_FORWARD({alias})',1)
            code.append(body); target = clone
        vals = ','.join(cpp_shapes)
        entries.append(f'    {{{original}, {target}, {{{vals}}}, {width}}},')
code += ['namespace {']
shape_entry = 'ProfileOperandShapeEntry' if decoded_match else 'OperandShapeEntry'
shape_template = ', DecodedOperandShape' if decoded_match else ''
code += [f'static const {shape_entry} shapes[] = {{'] + (entries or ['    {},']) + ['};',
         f'constexpr size_t shape_count = {len(entries)};']
registers = []
if not a.fused_only and (a.mode in (2, 3) or a.pin_mode >= 3):
    for name, (role, byte, group) in sorted(safe.items()):
        if group == 'Memory': continue  # Exact shapes only; never claim a register form.
        if group in ('Opcode', 'Address'):
            registers.append(f'    {{DispatchWrapper<op::{name}>, SelectEncodedOperand<op::{name}, PinWriteRole::{role}, {str(byte).lower()}, {str(group == "Opcode").lower()}>, PinOperandForm::{group}}},')
        else:
            registers.append(f'    {{DispatchWrapper<op::{name}>, SelectRegisterOperand<op::{name}, PinWriteRole::{role}, {str(byte).lower()}, {str(group == "Group").lower()}>}},')
code += ["""struct CompactImmediateEntry {
    HandlerFunc original;
    uint32_t immediate;
    HandlerFunc replacement;
};""", 'static constexpr CompactImmediateEntry compact_immediates[] = {']
for name, values in compact.items():
    for value in values:
        code.append(f'    {{DispatchWrapper<op::{name}>, {value}u, '
                    f'DispatchWrapper<op::{name}, OperandSpec::CompactImmediate<{value}u>>}},')
if not compact:
    code.append('    {},')
code += ['};', f'constexpr bool has_compact_immediates = {str(bool(compact)).lower()};',
         """HandlerFunc FindCompactImmediate(const DecodedInstruction* op) {
    if constexpr (has_compact_immediates) {
        if (!HasMem(op) || !compact_imm::HasFreePrefixBits(*op)) return nullptr;
        const auto imm = GetImm(op);
        if (!imm) return nullptr;
        using Variants = std::vector<CompactImmediateEntry>;
        static const auto index = [] {
            ankerl::unordered_dense::map<HandlerFunc, Variants> map;
            for (const auto& entry : compact_immediates) map[entry.original].push_back(entry);
            return map;
        }();
        const auto found = index.find(op->handler);
        if (found == index.end()) return nullptr;
        for (const auto& entry : found->second) {
            if (entry.immediate == imm) return entry.replacement;
        }
    }
    return nullptr;
}"""]
code += ['static const RegisterOperandEntry registers[] = {'] + (registers or ['    {},']) + ['};',
         f'constexpr size_t register_count = {len(registers)};',
         'const auto& OperandLookup() {',
         f'    static const OperandIndex<(register_count != 0), {str(bool(a.pin_specialization and not a.fused_only)).lower()}{shape_template}> index(shapes, shape_count, registers, register_count);',
         '    return index;', '}', '} // namespace',
         '''HandlerFunc CanonicalOperandHandler(HandlerFunc handler) {
    static const auto originals = [] {
        ankerl::unordered_dense::map<HandlerFunc, HandlerFunc> result;
        for (size_t i=0; i<shape_count; ++i) result[shapes[i].replacement] = shapes[i].original;
        if constexpr (has_compact_immediates) {
            for (const auto& entry : compact_immediates) result[entry.replacement] = entry.original;
        }
        DecodedInstruction dummy{};
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
}''', 'HandlerFunc LowerOperandHandler(const DecodedInstruction* op) {',
         '    if (const auto specialized = OperandLookup().Find(op)) return specialized;',
         '    return FindCompactImmediate(op);',
          '}',
         'HandlerFunc LowerFusionOperandHandler(const DecodedInstruction* op, HandlerFunc handler) {',
         '    return OperandLookup().Find(op, handler);', '}',
         'bool AcceptFusionOperandShape(const DecodedInstruction* op, HandlerFunc handler) {',
         '    return OperandLookup().Find(op, handler) != nullptr;',
         '}', '} // namespace fiberish']
path = pathlib.Path(a.output); path.parent.mkdir(parents=True, exist_ok=True); path.write_text('\n'.join(code) + '\n')
print(f'Operand lowering: {len(entries)} exact shapes, {len(registers)} register families, '
      f'{sum(map(len, compact.values()))} compact immediate handlers')
if decoded_match and skipped:
    print(f'Skipped {skipped} operand entries absent from the selected instruction or fusion source')

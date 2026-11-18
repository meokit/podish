#!/usr/bin/env python3
"""Bake bounded operand shapes and register-class policies; never specialize guest data."""
import argparse, json, pathlib, re
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
a = p.parse_args()
if not 0 <= a.budget <= 1024:
    p.error("budget must be between 0 and the bounded 1024-shape limit")
if a.mode not in range(4) or a.pin_mode not in range(4) or a.pin_specialization not in range(3):
    p.error("invalid lowering or pin mode")
source = pathlib.Path(a.source).read_text()
families = {name: int(op, 16) for name, op in re.findall(r'using\s+(\w+)\s*=\s*Encoding<0x([0-9A-Fa-f]+),', pathlib.Path(a.families).read_text())}
targets = {}
for op, name in re.findall(r'INSTRUCTION_FALLBACK\(0x([0-9A-Fa-f]+),\s*(\w+)\)', pathlib.Path(a.fallbacks).read_text()):
    targets.setdefault(name, set()).add(int(op, 16))
for name, fam in re.findall(r'INSTRUCTION_(?:FUSION_)?VARIANT\((\w+),\s*(\w+),', pathlib.Path(a.variants).read_text()):
    targets.setdefault(name, set()).add(families[fam])
def traits(op):
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
        if role == 'Rm': return 3
    if role == 'None' or group is True and ((shape >> 3) & 7) == 7: return 3
    field = (shape >> 3) if role == 'Reg' else shape
    idx = field & (3 if byte else 7)
    return 2 if idx == 0 else 1 if idx == 4 else 3

def body_for(name):
    marker = 'ATTR_PRESERVE_NONE int64_t ' + name + '('
    if marker not in source: return None
    start = source.index(marker); b = source.index('{', start); depth = 1; end = b + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}'); end += 1
    return source[start:end]
code = ['// Generated operand lowering. Inputs contain encoding facts, never immediates/addresses.',
        '#include "operand_specialization.h"', 'namespace fiberish {']
entries = []
if a.mode in (1, 3):
    for i, item in enumerate(json.loads(pathlib.Path(a.plan).read_text())[:a.budget]):
        if a.fused_only and item['kind'] != 'fused': continue
        original = item['original']; shapes = item['shapes']; width = item['width']
        if width not in range(1,5) or len(shapes) != 4 or any(not isinstance(v,int) or not 0 <= v < 2**48 for v in shapes):
            raise ValueError('Invalid operand plan entry: ' + original)
        if item['kind'] not in ('wrapper', 'fused') or item['kind'] == 'wrapper' and width != 1:
            raise ValueError('Invalid operand plan kind/width: ' + original)
        if item['kind'] == 'wrapper':
            match = re.fullmatch(r'DispatchWrapper<op::(\w+)>', original)
            if not match or match[1] not in targets: continue
            name = match[1]
            target = f'DispatchWrapper<op::{name}, ExactOperandPolicy<{shapes[0]}ULL, {keep_for(name,shapes[0])}>>'
        else:
            body = body_for(original)
            if body is None: continue
            ops = re.findall(r'RUN_SUPEROPCODE_OP\(op::(\w+),', body)
            if len(ops) != width: raise RuntimeError(original)
            clone = f'OperandClone_{i:03d}_{original}'
            body = body.replace(original, clone)
            pos = body.index('{') + 1
            body = body[:pos] + '\n' + ''.join(f'    AssumeOperandShape<{shapes[j]}ULL>(op + {j});\n' for j in range(width)) + body[pos:]
            keep = 3
            for name, shape in zip(ops, shapes): keep &= keep_for(name, shape)
            # Exits remain conservative; only the final normal tail may preserve known nonwrites.
            key = body.rfind('flags_cache FIBERCPU_PIN_RELOAD')
            if key >= 0:
                alias = f'OperandKeep_{i:03d}'
                code.append(f'using {alias} = ExactOperandPolicy<0, {keep}>;')
                body = body[:key] + body[key:].replace('flags_cache FIBERCPU_PIN_RELOAD',f'flags_cache FIBERCPU_PIN_FORWARD({alias})',1)
            code.append(body); target = clone
        vals = ','.join(str(s) + 'ULL' for s in shapes)
        entries.append(f'    {{{original}, {target}, {{{vals}}}, {width}}},')
code += ['namespace {']
code += ['static const OperandShapeEntry shapes[] = {'] + (entries or ['    {},']) + ['};',
         f'constexpr size_t shape_count = {len(entries)};']
registers = []
if not a.fused_only and (a.mode in (2, 3) or a.pin_mode == 3):
    for name, (role, byte, group) in sorted(safe.items()):
        if group == 'Memory': continue  # Exact shapes only; never claim a register form.
        if group in ('Opcode', 'Address'):
            registers.append(f'    {{DispatchWrapper<op::{name}>, SelectEncodedOperand<op::{name}, PinWriteRole::{role}, {str(byte).lower()}, {str(group == "Opcode").lower()}>, PinOperandForm::{group}}},')
        else:
            registers.append(f'    {{DispatchWrapper<op::{name}>, SelectRegisterOperand<op::{name}, PinWriteRole::{role}, {str(byte).lower()}, {str(group).lower()}>}},')
code += ['static const RegisterOperandEntry registers[] = {'] + (registers or ['    {},']) + ['};',
         f'constexpr size_t register_count = {len(registers)};',
         'const auto& OperandLookup() {',
         f'    static const OperandIndex<(register_count != 0), {str(bool(a.pin_specialization and not a.fused_only)).lower()}> index(shapes, shape_count, registers, register_count);',
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

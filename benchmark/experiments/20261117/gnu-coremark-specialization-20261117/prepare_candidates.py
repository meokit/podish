from pathlib import Path
import shutil,subprocess
out=Path(__file__).resolve().parent
for variant in ['masks','rotate']:
 root=out/(variant+'-source');shutil.copytree('libfibercpu',root/'libfibercpu',dirs_exist_ok=True)
 (root/'cmake').mkdir(exist_ok=True);shutil.copy2('cmake/CPM.cmake',root/'cmake/CPM.cmake')
 (root/'scripts').mkdir(exist_ok=True);shutil.copy2('scripts/gen_handler_reflection.py',root/'scripts/gen_handler_reflection.py')
 if variant=='masks':
  for name in ['ops_shift_bit.cpp','ops_alu.cpp']:
   p=root/'libfibercpu/src/ops'/name;s=p.read_text().replace('mod_mask = 0xC0','mod_mask = 0x03').replace('mod_val = 0xC0','mod_val = 0x03');p.write_text(s)
 else:
  p=root/'libfibercpu/src/ops/ops_shift_bit_impl.h';s=p.read_text();needle='namespace op {\n';assert needle in s
  code='''
// Register-only immediate rotations: decoder-selected width and flag liveness.
// The 5-bit masked count (not its modulo-width value) controls CF/OF updates.
template <typename T, bool Right, bool UpdateFlags>
FORCE_INLINE LogicFlow OpRotateRegImmediate(LogicFuncParams) {
    const uint8_t rm = op->modrm & 7;
    const uint32_t original = GetReg(state, rm);
    const T value = static_cast<T>(original);
    const uint8_t count = imm & 31;
    const T result = Right ? std::rotr(value, count) : std::rotl(value, count);
    if constexpr (UpdateFlags) {
        if (count != 0) {
            constexpr unsigned width = sizeof(T) * 8;
            const bool cf = Right ? (result >> (width - 1)) & 1 : result & 1;
            AssignCF(flags_cache, cf);
            if (count == 1) {
                const bool msb = (result >> (width - 1)) & 1;
                const bool other = Right ? (result >> (width - 2)) & 1 : cf;
                AssignOF(flags_cache, msb != other);
            }
        }
    }
    if constexpr (sizeof(T) == 2)
        SetReg(state, rm, (original & 0xFFFF0000u) | result);
    else
        SetReg(state, rm, result);
    return LogicFlow::Continue;
}

'''
  s=s.replace(needle,needle+code,1);p.write_text(s)
  p=root/'libfibercpu/src/ops/ops_shift_bit.cpp';s=p.read_text();needle='void RegisterShiftBitOps() {'
  helper='''template <typename T, bool Right>
static void RegisterRotateRegImmediate() {
    SpecCriteria c;
    c.mod_mask = 3;
    c.mod_val = 3;
    c.reg_mask = 7;
    c.reg_val = Right ? 1 : 0;
    c.prefix_mask = prefix::OPSIZE;
    c.prefix_val = sizeof(T) == 2 ? prefix::OPSIZE : 0;
    DispatchRegistrar<op::OpRotateRegImmediate<T, Right, true>>::RegisterSpecialized(0xC1, c);
    c.no_flags = true;
    DispatchRegistrar<op::OpRotateRegImmediate<T, Right, false>>::RegisterSpecialized(0xC1, c);
}

'''
  s=s.replace(needle,helper+needle,1).replace('    // Specializations','    RegisterRotateRegImmediate<uint16_t, false>();\n    RegisterRotateRegImmediate<uint16_t, true>();\n    RegisterRotateRegImmediate<uint32_t, false>();\n    RegisterRotateRegImmediate<uint32_t, true>();\n\n    // Specializations',1);p.write_text(s)
 for name in ['ops_shift_bit.cpp','ops_alu.cpp','ops_shift_bit_impl.h']:
  original=Path('libfibercpu/src/ops')/name;changed=root/'libfibercpu/src/ops'/name
  if original.read_bytes()!=changed.read_bytes():
   patch=subprocess.run(['diff','-u','--label','a/'+str(original),'--label','b/'+str(original),str(original),str(changed)],text=True,stdout=subprocess.PIPE).stdout
   with (out/(variant+'.patch')).open('a') as f:f.write(patch)

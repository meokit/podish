from pathlib import Path
import shutil,re
out=Path(__file__).resolve().parent;repo=out.parents[1];root=out/'profile-source';shutil.copytree(repo/'libfibercpu',root/'libfibercpu',dirs_exist_ok=True,ignore=shutil.ignore_patterns('build*','__pycache__'));(root/'cmake').mkdir(exist_ok=True);shutil.copyfile(repo/'cmake/CPM.cmake',root/'cmake/CPM.cmake');(root/'scripts').mkdir(exist_ok=True);shutil.copyfile(repo/'scripts/gen_handler_reflection.py',root/'scripts/gen_handler_reflection.py')
p=root/'libfibercpu/src/state.h';s=p.read_text().replace('struct EmuState {','struct EmuState {\n    uint64_t profile_reg_reads[9] = {};\n    uint64_t profile_reg_writes[9] = {};\n    uint64_t profile_reg_ea[9] = {};');p.write_text(s)
p=root/'libfibercpu/src/exec_utils.h';s=p.read_text();s=s.replace('return &state->ctx.regs[reg_idx];','++state->profile_reg_reads[reg_idx]; ++state->profile_reg_writes[reg_idx]; return &state->ctx.regs[reg_idx];');s=s.replace('return *GetRegPtr(state, reg_idx);','++state->profile_reg_reads[reg_idx]; return state->ctx.regs[reg_idx];');s=s.replace('*GetRegPtr(state, reg_idx) = val;','++state->profile_reg_writes[reg_idx]; state->ctx.regs[reg_idx] = val;')
s=s.replace('FORCE_INLINE void SetReg8', 'FORCE_INLINE uint32_t ProfileEaRead(EmuState* state, uint8_t offset) { ++state->profile_reg_ea[offset / 4]; return GetReg(state, offset / 4); }\n\nFORCE_INLINE void SetReg8',1)
s=re.sub(r'\*\(const uint32_t\*\)\(regs_base \+ (memdesc::(?:Base|Index)Offset\(ea_desc\))\)',r'ProfileEaRead(state, \1)',s);p.write_text(s)
p=root/'libfibercpu/src/ops/ops_data_mov_impl.h';s=p.read_text();s=re.sub(r'state->ctx.regs\[([^\n;]+?)\] = ([^;]+);',r'SetReg(state, \1, \2);',s);s=re.sub(r'state->ctx.regs\[([^\n;]+?)\]',r'GetReg(state, \1)',s);s=re.sub(r'\*\(const uint32_t\*\)\(regs_base \+ (memdesc::(?:Base|Index)Offset\(ea_desc\))\)',r'ProfileEaRead(state, \1)',s);p.write_text(s)
p=root/'libfibercpu/src/bindings.cpp';s=p.read_text();needle='int X86_Destroy(EmuState* state) {';assert needle in s;s=s.replace(needle,needle+'''\n    if (state) if (const char* path = std::getenv("FIBERCPU_REG_PROFILE")) if (FILE* f = std::fopen(path, "a")) {
        std::fprintf(f, "{\\\"reads\\\":[");
        for (int i=0;i<9;i++) std::fprintf(f, "%s%llu", i ? "," : "", (unsigned long long)state->profile_reg_reads[i]);
        std::fprintf(f, "],\\\"writes\\\":[");
        for (int i=0;i<9;i++) std::fprintf(f, "%s%llu", i ? "," : "", (unsigned long long)state->profile_reg_writes[i]);
        std::fprintf(f, "],\\\"ea_reads\\\":[");
        for (int i=0;i<9;i++) std::fprintf(f, "%s%llu", i ? "," : "", (unsigned long long)state->profile_reg_ea[i]);
        std::fprintf(f, "]}\\n"); std::fclose(f);
    }
''');s='#include <cstdio>\n#include <cstdlib>\n'+s;p.write_text(s)
shutil.copytree(repo/'artifacts/coremark-3800-20261117/observed-cli',out/'profile-cli',dirs_exist_ok=True)

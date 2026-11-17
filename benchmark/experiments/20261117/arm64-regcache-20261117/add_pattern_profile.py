from pathlib import Path
import shutil,re
out=Path(__file__).resolve().parent;root=out/'profile-source/libfibercpu/src'
p=root/'state.h';s=p.read_text();key='''struct RegPatternKey { std::array<uint64_t,5> words{}; bool operator==(const RegPatternKey&) const = default; };
struct RegPatternHash { uint64_t operator()(const RegPatternKey& k) const { uint64_t h=0; for(auto x:k.words) h ^= x + 0x9e3779b97f4a7c15ULL + (h<<6) + (h>>2); return h; } };
''';s=s.replace('struct EmuState {',key+'\nstruct EmuState {\n    ankerl::unordered_dense::map<RegPatternKey,uint64_t,RegPatternHash> profile_patterns;');# keep ctx first
s=s.replace('    ankerl::unordered_dense::map<RegPatternKey,uint64_t,RegPatternHash> profile_patterns;\n','').replace('    Context ctx;','    Context ctx;\n    ankerl::unordered_dense::map<RegPatternKey,uint64_t,RegPatternHash> profile_patterns;');p.write_text(s)
p=root/'dispatch.h';s=p.read_text();needle='template <LogicFunc Target>\nATTR_PRESERVE_NONE int64_t DispatchWrapper';helper='''FORCE_INLINE void RecordRegPattern(EmuState* state, HandlerFunc handler, DecodedOp* op, int width) {
    RegPatternKey key; key.words[0] = reinterpret_cast<uintptr_t>(handler);
    for (int i=0;i<width;i++) {
        const uint32_t ea = op->meta.flags.has_mem ? GetExt(op)->data.ea_desc : 0;
        key.words[i+1] = uint64_t(op->modrm) | (uint64_t(op->prefixes.all)<<8) | (uint64_t(ea)<<16);
        op = NextOp(op);
    }
    ++state->profile_patterns[key];
}
''';assert needle in s;s=s.replace(needle,helper+'\n'+needle);needle='    mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);';s=s.replace(needle,'    RecordRegPattern(state, DispatchWrapper<Target>, op, 1);\n'+needle);p.write_text(s)
p=out/'profile.generated.cpp';s=Path('artifacts/coremark-3800-20261117/observed.generated.cpp').read_text();pat=r'(ATTR_PRESERVE_NONE int64_t (SuperOpcode_([24])_\w+)\([^{}]+?flags_cache\) \{)';s,n=re.subn(pat,lambda m:m[1]+'\n    RecordRegPattern(state, '+m[2]+', op, '+m[3]+');',s);assert n==167,n;p.write_text(s)
p=root/'bindings.cpp';s=p.read_text();needle='    if (state) if (const char* path = std::getenv("FIBERCPU_REG_PROFILE"))';code='''    if (state) if (const char* path = std::getenv("FIBERCPU_PATTERN_PROFILE")) if (FILE* f = std::fopen(path, "a")) {
        for (const auto& [k,v]:state->profile_patterns) {
            const char* name = X86_GetHandlerSymbolById(X86_GetHandlerId(reinterpret_cast<void*>(k.words[0])));
            std::fprintf(f, "{\\\"symbol\\\":\\\"%s\\\",\\\"shapes\\\":[%llu,%llu,%llu,%llu],\\\"count\\\":%llu}\\n", name?name:"unknown", (unsigned long long)k.words[1], (unsigned long long)k.words[2], (unsigned long long)k.words[3], (unsigned long long)k.words[4], (unsigned long long)v);
        }
        std::fclose(f);
    }
''';assert needle in s;s=s.replace(needle,code+needle);p.write_text(s)

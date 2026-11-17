from pathlib import Path
out=Path(__file__).resolve().parent;src=out/'profile-source/libfibercpu/src'
p=src/'state.h';s=p.read_text();needle='    Context ctx;';assert 'profile_reg_epoch' not in s;s=s.replace(needle,needle+'''\n    uint64_t profile_reg_epoch = 1;
    uint64_t profile_reg_last_access[9] = {};
    uint64_t profile_reg_last_write[9] = {};
    uint64_t profile_reg_livein[9] = {};
    uint64_t profile_reg_entry_writes[9] = {};
''');p.write_text(s)
p=src/'exec_utils.h';s=p.read_text();s=s.replace('++state->profile_reg_reads[reg_idx];','''++state->profile_reg_reads[reg_idx];
    if (state->profile_reg_last_access[reg_idx] != state->profile_reg_epoch) {
        state->profile_reg_last_access[reg_idx] = state->profile_reg_epoch;
        ++state->profile_reg_livein[reg_idx];
    }
''');s=s.replace('++state->profile_reg_writes[reg_idx];','''++state->profile_reg_writes[reg_idx];
    state->profile_reg_last_access[reg_idx] = state->profile_reg_epoch;
    if (state->profile_reg_last_write[reg_idx] != state->profile_reg_epoch) {
        state->profile_reg_last_write[reg_idx] = state->profile_reg_epoch;
        ++state->profile_reg_entry_writes[reg_idx];
    }
''');p.write_text(s)
p=src/'dispatch.h';s=p.read_text().replace('    RegPatternKey key;','    ++state->profile_reg_epoch;\n    RegPatternKey key;');p.write_text(s)
p=src/'bindings.cpp';s=p.read_text();needle='        std::fprintf(f, "]}\\n"); std::fclose(f);';assert needle in s;s=s.replace(needle,'''        std::fprintf(f, "],\\\"livein\\\":[");
        for (int i=0;i<9;i++) std::fprintf(f, "%s%llu", i ? "," : "", (unsigned long long)state->profile_reg_livein[i]);
        std::fprintf(f, "],\\\"entry_writes\\\":[");
        for (int i=0;i<9;i++) std::fprintf(f, "%s%llu", i ? "," : "", (unsigned long long)state->profile_reg_entry_writes[i]);
        std::fprintf(f, "]}\\n"); std::fclose(f);''');p.write_text(s)

from pathlib import Path
import shutil
out=Path(__file__).resolve().parent;tool=out/'baking-tool'
shutil.copytree('Podish.PerfTools',tool,dirs_exist_ok=True,ignore=shutil.ignore_patterns('bin','obj'))
p=tool/'Program.SuperopcodeSequences.cs';s=p.read_text();needle='        string Name(int index) =>'
code='''        // Bake semantic specializations as separate exact-match sequences.
        // Keep original handlers for memory forms and execute specialized logic in
        // the new variants; do not normalize back to generic logic at runtime.
        var selectedPatternCount = patterns.Count;
        var specializationFamilies = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["op::OpAdd_EvGv"] = ["op::OpAdd_EvGv_Eax"],
            ["op::OpGroup2_EvIb_Shl"] = ["op::OpGroup2_EvIb_Shl_ModReg"],
            ["op::OpGroup2_Ev1_Shl"] = ["op::OpGroup2_Ev1_Shl_ModReg"],
            ["op::OpGroup2_EvCl_Shl"] = ["op::OpGroup2_EvCl_Shl_ModReg"],
            ["op::OpGroup2_EvIb_Shr"] = ["op::OpGroup2_EvIb_Shr_ModReg"],
            ["op::OpGroup2_Ev1_Shr"] = ["op::OpGroup2_Ev1_Shr_ModReg"],
            ["op::OpGroup2_EvCl_Shr"] = ["op::OpGroup2_EvCl_Shr_ModReg"],
            ["op::OpGroup2_EvIb_Sar"] = ["op::OpGroup2_EvIb_Sar_ModReg"],
            ["op::OpGroup2_Ev1_Sar"] = ["op::OpGroup2_Ev1_Sar_ModReg"],
            ["op::OpGroup2_EvCl_Sar"] = ["op::OpGroup2_EvCl_Sar_ModReg"]
        };
        foreach (var pattern in patterns.Take(selectedPatternCount).ToArray())
        {
            var variants = new List<string[]> { Array.Empty<string>() };
            foreach (var op in pattern)
            {
                var choices = specializationFamilies.TryGetValue(op, out var family)
                    ? new[] { op }.Concat(family).ToArray() : new[] { op };
                variants = variants.SelectMany(prefix => choices.Select(choice => prefix.Append(choice).ToArray())).ToList();
            }
            foreach (var variant in variants)
                if (seen.Add(string.Join('\\t', variant))) patterns.Add(variant);
        }
'''
assert needle in s;s=s.replace(needle,code+needle)
s=s.replace('        builder.AppendLine("// generated_pattern_count=" + patterns.Count);','        builder.AppendLine("// selected_pattern_count=" + selectedPatternCount);\n        builder.AppendLine("// specialization_expansion_count=" + (patterns.Count - selectedPatternCount));\n        builder.AppendLine("// generated_pattern_count=" + patterns.Count);')
p.write_text(s)
# Composed candidate: both manual instruction changes, with old baking retained.
root=out/'specialized-source';shutil.copytree(out/'rotate-source',root,dirs_exist_ok=True)
for name in ['ops_shift_bit.cpp','ops_alu.cpp']:
 p=root/'libfibercpu/src/ops'/name;s=p.read_text().replace('mod_mask = 0xC0','mod_mask = 0x03').replace('mod_val = 0xC0','mod_val = 0x03');p.write_text(s)

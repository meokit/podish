using System.Text.Json;
using System.Globalization;
using Fiberish.X86.Native;
using Xunit;

namespace Fiberish.X86.Tests.Regression;

public sealed unsafe class InstructionCatalogTests
{
    private static X86Native.InstructionVariant[] Catalog()
    {
        var entries = new X86Native.InstructionVariant[X86Native.GetInstructionVariantCount()];
        for (var i = 0; i < entries.Length; ++i)
            Assert.Equal(1, X86Native.GetInstructionVariant(i, out entries[i]));
        return entries;
    }

    [Fact]
    public void EveryDeclaredVariantHasAStableIdentityAndReflectedEntry()
    {
        var entries = Catalog();
        Assert.Equal(entries.Length, entries.Select(x => x.variant_key).Distinct().Count());
        var staticSpecialization = !X86Native.GetOptimizationConfig().Contains("static=OFF;");
        foreach (var v in entries)
        {
            Assert.Equal(1u, v.schema_version);
            Assert.InRange(v.opcode, 0u, 1023u);
            Assert.StartsWith("op::Op", v.LogicName);
            Assert.NotEmpty(v.FamilyName);
            if (staticSpecialization || v.is_fallback != 0)
                Assert.NotEqual(IntPtr.Zero, v.handler);
            if (v.handler != IntPtr.Zero)
                Assert.True(X86Native.GetHandlerId(v.handler) >= 0);
        }
        Assert.Equal(0, X86Native.GetInstructionVariant(-1, out _));
        Assert.Equal(0, X86Native.GetInstructionVariant(entries.Length, out _));
    }

    [Fact]
    public void Group2HasACanonicalHandlerForEveryCountSourceAndWidth()
    {
        var entries = Catalog();
        (uint Opcode, string Logic)[] forms =
        [
            (0xc0, "OpGroup2_EbIb"), (0xc1, "OpGroup2_EvIb"),
            (0xd0, "OpGroup2_Eb1"), (0xd1, "OpGroup2_Ev1"),
            (0xd2, "OpGroup2_EbCl"), (0xd3, "OpGroup2_EvCl")
        ];
        foreach (var (opcode, logic) in forms)
        {
            var entry = Assert.Single(entries.Where(v => v.opcode == opcode));
            Assert.NotEqual(0u, entry.is_fallback);
            Assert.Equal("op::" + logic, entry.LogicName);
        }
    }

    [Fact]
    public void MandatoryPrefixesSelectDistinctIdentitiesWithoutOperandConstraints()
    {
        var entries = Catalog();
        var aligned = entries.Single(x => x.opcode == 0x16f && x.LogicName == "op::OpMovdqa_Load");
        var unaligned = entries.Single(x => x.opcode == 0x16f && x.LogicName == "op::OpMovdqu_Load");
        Assert.Equal(64u, aligned.constraints[6]); // 0x66
        Assert.Equal(64u, aligned.constraints[7]);
        Assert.Equal(2u, unaligned.constraints[6]); // 0xf3
        Assert.Equal(2u, unaligned.constraints[7]);
        Assert.NotEqual(aligned.variant_key, unaligned.variant_key);
        foreach (var variant in entries.Where(x => x.is_fallback == 0))
            for (var i = 0; i < 19; ++i)
                if (i != 6 && i != 7)
                    Assert.Equal(0u, variant.constraints[i]);
    }

    [Fact]
    public void DeclarationsMatchTheCapturedArchitecturalSelectionCatalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Podish.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var file = Path.Combine(root.FullName, "Fiberish.X86.Tests", "Regression", "Fixtures", "instruction-selection-baseline.tsv");
        var expected = File.ReadAllLines(file).Where(x => !x.StartsWith('#')).ToArray();
        var actual = Catalog().Select(SelectionRow).ToArray();
        Assert.Equal(expected, actual);
    }

    private static string SelectionRow(X86Native.InstructionVariant v)
    {
        var fields = new List<string> { v.is_fallback != 0 ? "F" : "V", v.opcode.ToString(CultureInfo.InvariantCulture), v.LogicName };
        if (v.is_fallback == 0)
            for (var i = 0; i < 19; ++i) fields.Add(v.constraints[i].ToString(CultureInfo.InvariantCulture));
        return string.Join('\t', fields);
    }

    [Fact]
    public void FusionSnapshotMatchesTheNativeCatalogFingerprintAndAliases()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Podish.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "libfibercpu", "src", "generated", "instruction_fusion_families.json")));
        Assert.Equal(X86Native.GetInstructionCatalogHash(), doc.RootElement.GetProperty("source_sha256").GetString());
        var expected = doc.RootElement.GetProperty("families").EnumerateObject()
            .SelectMany(x => x.Value.EnumerateArray().Select(v => (x.Name, v.GetString()!))).Order().ToArray();
        var actual = Catalog().Where(x => x.bake_with_parent != 0)
            .Select(x => (x.FusionParent, x.LogicName)).Order().ToArray();
        Assert.Equal(expected, actual);
    }
}

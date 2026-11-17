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
        Assert.Equal(411, entries.Count(x => x.is_fallback != 0));
        Assert.Equal(503, entries.Count(x => x.is_fallback == 0));
        Assert.Equal(entries.Length, entries.Select(x => x.variant_key).Distinct().Count());
        foreach (var v in entries)
        {
            Assert.Equal(1u, v.schema_version);
            Assert.InRange(v.opcode, 0u, 1023u);
            Assert.StartsWith("op::Op", v.LogicName);
            Assert.NotEmpty(v.FamilyName);
            Assert.NotEqual(IntPtr.Zero, v.handler);
            Assert.True(X86Native.GetHandlerId(v.handler) >= 0);
        }
        Assert.Equal(0, X86Native.GetInstructionVariant(-1, out _));
        Assert.Equal(0, X86Native.GetInstructionVariant(entries.Length, out _));
    }

    [Fact]
    public void OperandRolesGroupExtensionAndMemoryShapesHaveIndependentConstraints()
    {
        var entries = Catalog();
        var add = entries.Single(x => x.opcode == 0x83 && x.LogicName == "op::OpGroup1_EvIb_Add_32_NoFlags_Eax");
        Assert.Equal(3u, add.constraints[0]);
        Assert.Equal(3u, add.constraints[1]);
        Assert.Equal(7u, add.constraints[2]); // /0 is the ADD extension.
        Assert.Equal(0u, add.constraints[3]);
        Assert.Equal(7u, add.constraints[4]); // EAX is the r/m destination.
        Assert.Equal(0u, add.constraints[5]);
        Assert.Equal(64u, add.constraints[6]);
        Assert.Equal(0u, add.constraints[7]);
        Assert.Equal(1u, add.constraints[8]);
        var mov = entries.Single(x => x.LogicName == "op::OpMov_Load_Eax_EspBaseNoIndexNoSegment");
        Assert.Equal(0u, mov.constraints[0]); // A memory shape must not imply Mod=3.
        Assert.Equal(7u, mov.constraints[2]);
        Assert.Equal(0u, mov.constraints[3]); // EAX is ModRM.reg for a load.
        Assert.Equal(4u, mov.constraints[5]); // SIB encoding, not a guest ESP operand.
        Assert.Equal(16u, mov.constraints[10]);
        Assert.Equal(32u, mov.constraints[12]);
        Assert.Equal(7u, mov.constraints[15]);
        Assert.Equal(0u, mov.constraints[16]); // Default segment; actual base is still architectural state.
    }

    [Fact]
    public void DeclarationsMatchTheCapturedPreRefactorSelectionCatalog()
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
            for (var i = 0; i < 17; ++i) fields.Add(v.constraints[i].ToString(CultureInfo.InvariantCulture));
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

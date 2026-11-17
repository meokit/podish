using System.Text.Json;
using Xunit;

namespace Fiberish.X86.Tests.Fex;

public sealed class FexCatalogTests
{
    [Fact]
    public void PinnedInventoryAndRunnableAssetsAreComplete()
    {
        var catalog = FexAssets.ReadCatalog();
        var expected = new Dictionary<string, int>
        {
            ["Primary"] = 50, ["PrimaryGroup"] = 12, ["Secondary"] = 4, ["SecondaryModRM"] = 2,
            ["TwoByte"] = 2, ["VEX"] = 32, ["X87"] = 138, ["FEX_bugs"] = 18, ["root"] = 2
        };
        foreach (var group in catalog.Tests.GroupBy(test => test.Id.Contains('/') ? test.Id.Split('/')[0] : "root"))
            Assert.Equal(expected[group.Key], group.Count());
        Assert.Equal(204, catalog.Tests.Count(test => test.Status == "ready"));
        Assert.Equal(137, catalog.Tests.Count(test => test.Status == "ready" && test.Id.StartsWith("X87/", StringComparison.Ordinal)));

        foreach (var entry in catalog.Tests)
        {
            var manifest = FexAssets.ReadManifest(entry);
            if (manifest.Binary != null) FexAssets.ReadBinary(manifest);
            if (entry.Status == "ready") FexConfig.Parse(manifest.Config).RequireRunnable();
            else Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        }
        var rows = Enumerable.Range(0, 8).SelectMany(FexAssets.Cases).Select(row => (string)row[0]).ToArray();
        Assert.Equal(204, rows.Length);
        Assert.Equal(rows.Length, rows.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void UndefinedFyl2xp1InputIsExplicitlyInventoried()
    {
        var entry = Assert.Single(FexAssets.ReadCatalog().Tests, test => test.Id == "X87/D9_F9");
        Assert.Equal("blocked", entry.Status);
        Assert.Contains("outside its architecturally defined range", entry.Reason);
        // Keep the original upstream binary and expected data available for audit.
        Assert.NotEmpty(FexAssets.ReadBinary(FexAssets.ReadManifest(entry)));
    }

    [Theory]
    [InlineData("{\"Mode\":\"32BIT\",\"Unknown\":{}}")]
    [InlineData("{\"Mode\":\"64BIT\"}")]
    [InlineData("{\"Mode\":\"32BIT\",\"Mode\":\"32BIT\"}")]
    [InlineData("{\"Mode\":\"32BIT\",\"RegData\":{\"UNKNOWN\":\"0\"}}")]
    [InlineData("{\"Mode\":\"32BIT\",\"HostFeatures\":[\"Unknown\"]}")]
    [InlineData("{\"Mode\":\"32BIT\",\"Env\":{\"Unknown\":\"1\"}}")]
    public void UnsupportedConfigurationNeverBecomesAnImplicitPass(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => FexConfig.Parse(document.RootElement).RequireRunnable());
    }

    [Fact]
    public void MemoryInitializerUsesFexLittleEndianWordSemantics()
    {
        Assert.Equal(new byte[] { 0x44, 0x43, 0x42, 0x41, 0xEF, 0xBE, 0xAD, 0xDE },
            FexConfig.MemoryBytes("0x41424344 0xdeadbeef"));
    }
}

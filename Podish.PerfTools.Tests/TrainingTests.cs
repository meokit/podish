using Xunit;
using Tool = Podish.PerfTools.Program;

namespace Podish.PerfTools.Tests;

public sealed class TrainingTests
{
    [Fact]
    public void DefaultTableReproducesTheReviewedLong4x32Manifest()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Podish.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var manifest = Path.Combine(root.FullName, "benchmark", "superopcode", "manifests", "gnu-long4x32-v1.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
        var patterns = document.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(64, patterns.Length);
        Assert.Equal(32, patterns.Count(pattern => pattern.GetProperty("pair").GetArrayLength() == 2));
        Assert.Equal(32, patterns.Count(pattern => pattern.GetProperty("pair").GetArrayLength() == 4));
        Assert.Equal(Tool.GenerateSuperopcodes(manifest, 64),
            File.ReadAllText(Path.Combine(root.FullName, "libfibercpu", "src", "generated", "superopcodes.generated.cpp")));
    }

    [Fact]
    public void LongSequencePolicyAllowsOnlyOrdinaryPrefixesAndOptionalFinalBranch()
    {
        Assert.True(Tool.TrainingSequenceAllowed(["op::OpMov_Load_Eax", "op::OpCmp_EaxImm", "op::OpJcc_NE_Rel32"]));
        Assert.False(Tool.TrainingSequenceAllowed(["op::OpCmp_EaxImm", "op::OpJcc_NE_Rel32", "op::OpMov_Load_Eax"]));
        Assert.False(Tool.TrainingSequenceAllowed(["op::OpMovs_Byte", "op::OpCmp_EaxImm", "op::OpJcc_NE_Rel32"]));
        Assert.False(Tool.TrainingSequenceAllowed(["op::OpInc_Reg"]));
    }

    [Fact]
    public void VariableWidthGenerationIsStableLongestFirstAndChecksRealSlots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "sequence-generator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "selected.json");
            File.WriteAllText(path, """
                {"candidates":[
                  {"pair":["op::OpInc_Reg_NF","op::OpInc_Reg_NF"]},
                  {"pair":["op::OpInc_Reg_NF","op::OpInc_Reg_NF","op::OpInc_Reg"]},
                  {"pair":["op::OpInc_Reg_NF","op::OpInc_Reg_NF","op::OpInc_Reg_NF","op::OpInc_Reg"]}
                ]}
                """);
            var generated = Tool.GenerateSuperopcodes(path, 3);
            Assert.Equal(generated, Tool.GenerateSuperopcodes(path, 3));
            Assert.DoesNotContain('\r', generated);
            Assert.Contains("third->len == 0", generated); Assert.Contains("fourth->len != 0", generated);
            Assert.Contains("constituent3", generated);
            var bucket = generated[generated.IndexOf("std::vector<GeneratedSequenceMatch>{", StringComparison.Ordinal)..];
            Assert.True(bucket.IndexOf("SuperOpcode_4_", StringComparison.Ordinal) < bucket.IndexOf("SuperOpcode_3_", StringComparison.Ordinal));
            Assert.True(bucket.IndexOf("SuperOpcode_3_", StringComparison.Ordinal) < bucket.IndexOf("SuperOpcode_2_", StringComparison.Ordinal));
            File.WriteAllText(path, "{\"candidates\":[{\"pair\":[\"OpA\",\"OpB\",\"OpC\",\"OpD\",\"OpE\"]}]}");
            Assert.Throws<InvalidDataException>(() => Tool.GenerateSuperopcodes(path, 5));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DensityScalingPreservesOpportunityRatesInsteadOfEqualizingIoAndCompute()
    {
        const string cpu = "op::OpCmp_EvGv_32_ModReg\top::OpJcc_NE_Rel32";
        const string io = "op::OpMov_Load_Eax\top::OpMov_Load_Edx";
        var cpuCell = Tool.ScaleSelectionDistribution(new Dictionary<string, double> { [cpu] = 0.5 }, 100);
        var ioCell = Tool.ScaleSelectionDistribution(new Dictionary<string, double> { [io] = 1.0 }, 1);
        Assert.Equal(cpu, Tool.RankSelectionPairs([cpuCell, ioCell], 1, "density", 0).Single());
        Assert.Equal(50, cpuCell[cpu]);
        Assert.Equal(1, ioCell[io]);
        Assert.Equal(cpu, Tool.RankSelectionPairs([cpuCell, ioCell], 1, "density-branch", 0).Single());
        Assert.Throws<ArgumentOutOfRangeException>(() => Tool.ScaleSelectionDistribution(cpuCell, double.NaN));
    }

    [Fact]
    public void OverlapModelDoesNotSpendTheNextSlotOnAnAlreadyConsumedMiddleOp()
    {
        const string ab = "op::OpMov_Load_Eax\top::OpMov_Load_Edx";
        const string bc = "op::OpMov_Load_Edx\top::OpCmp_EaxImm";
        const string xy = "op::OpTest_EbGb\top::OpJcc_NE_Rel32";
        var distribution = new Dictionary<string, double> { [ab] = 0.4, [bc] = 0.4, [xy] = 0.2 };
        Assert.Equal(new[] { ab, bc }, Tool.RankSelectionPairs([distribution], 2, "slope", 0));
        Assert.Equal(new[] { ab, xy }, Tool.RankSelectionPairs([distribution], 2, "overlap", 0));
        Assert.All(Tool.RankSelectionPairs([distribution], 3, "branch", 0), key => Assert.EndsWith("op::OpJcc_NE_Rel32", key));
    }

    [Theory]
    [InlineData("records-v1")]
    [InlineData("logs-v2")]
    [InlineData("source-v2")]
    [InlineData("entropy-v2")]
    public void CorporaAreDeterministicPrefixStableAndExerciseAllTextFilters(string corpus)
    {
        var small = Tool.CreateTrainingText(corpus, 12345, 16384);
        var large = Tool.CreateTrainingText(corpus, 12345, 65536);
        Assert.Equal(small, Tool.CreateTrainingText(corpus, 12345, 16384));
        Assert.StartsWith(small.Text, large.Text);
        Assert.Contains(small.FixedPattern, small.Text);
        Assert.Matches(new System.Text.RegularExpressions.Regex(small.RegexPattern,
            System.Text.RegularExpressions.RegexOptions.Multiline), small.Text);
        Assert.NotEqual(small.Text, small.SedExpected);
        Assert.DoesNotContain('\0', large.Text);
    }

    [Fact]
    public void SizeSlopeRemovesStartupAndNeverFabricatesNegativeCounts()
    {
        var slope = Tool.PositiveTrainingSlope(new Dictionary<string, double> { ["startup"] = 100, ["payload"] = 200, ["noise"] = 9 },
            new Dictionary<string, double> { ["startup"] = 100, ["payload"] = 20, ["noise"] = 10 }, 90);
        Assert.Single(slope);
        Assert.Equal(2, slope["payload"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Tool.PositiveTrainingSlope(new Dictionary<string, double>(), new Dictionary<string, double>(), 0));
    }

    [Fact]
    public void CostRulePenalizesLargeMemoryHandlersWithoutChangingLegality()
    {
        const string cheap = "op::OpCmp_EvGv_32_ModReg\top::OpJcc_NE_Rel32";
        const string expensive = "op::OpMov_Load_Eax\top::OpMov_Load_Edx";
        Assert.True(Tool.EstimatedPairCost(expensive) > Tool.EstimatedPairCost(cheap));
        var distribution = new Dictionary<string, double> { [cheap] = 0.4, [expensive] = 0.6 };
        Assert.Equal(expensive, Tool.RankSelectionPairs([distribution], 1, "slope", 0).Single());
        Assert.Equal(cheap, Tool.RankSelectionPairs([distribution], 1, "cost", 0.75).Single());
        Assert.Equal(Tool.RankSelectionPairs([distribution], 2, "coverage", 0), Tool.RankSelectionPairs([distribution], 2, "coverage", 0));
    }

    [Fact]
    public void DistilledSuitesDoNotRetainTheHeavySizeSweep()
    {
        var heavy = new Tool.TrainingSuite(1, "heavy", 12345, 8388608, 1,
            ["tar-create", "gzip-compress", "sed-transform", "grep-fixed"],
            [131072, 524288, 1048576], 128, 0.95, 0.80, 0.12, 1800);
        Tool.ValidateTrainingSuite(heavy);
        foreach (var size in heavy.LightSizes)
        {
            var light = Tool.CreateLightTrainingSuite(heavy, size);
            Tool.ValidateTrainingSuite(light);
            Assert.Equal(size, light.Bytes);
            Assert.Empty(light.LightSizes);
            Assert.Equal(heavy.Cases, light.Cases);
        }
        Assert.Throws<InvalidDataException>(() => Tool.ValidateTrainingSuite(heavy with { MinimumHotMass = double.NaN }));
        Assert.Throws<InvalidDataException>(() => Tool.ValidateTrainingSuite(heavy with { Seed = 0 }));
    }

    [Fact]
    public void IdenticalDistributionsHavePerfectQuality()
    {
        var reference = new Dictionary<string, double> { ["A"] = 0.75, ["B"] = 0.25 };
        var quality = Tool.CompareTrainingDistributions(reference, reference, 2);
        Assert.Equal(1, quality.HotMass);
        Assert.Equal(1, quality.TopOverlap);
        Assert.Equal(0, quality.JensenShannon);
    }

    [Fact]
    public void DisjointDistributionsCannotBeAcceptedAsDistillation()
    {
        var quality = Tool.CompareTrainingDistributions(new() { ["A"] = 1 }, new() { ["B"] = 1 }, 1);
        Assert.Equal(0, quality.HotMass);
        Assert.Equal(0, quality.TopOverlap);
        Assert.Equal(1, quality.JensenShannon);
    }

    [Theory]
    [InlineData("fiberish::DispatchWrapper<&fiberish::op::OpMov_Load_Eax>", "op::OpMov_Load_Eax")]
    [InlineData("__int64 fiberish::DispatchWrapper<&enum fiberish::LogicFlow fiberish::op::OpAdd_EaxImm(struct fiberish::EmuState *)>", "op::OpAdd_EaxImm")]
    public void CompilerSpecificSpellingNeverBecomesProfileIdentity(string symbol, string expected) =>
        Assert.Equal(expected, Tool.NormalizeTrainingHandler(symbol));

    [Theory]
    [InlineData("0x12345678")]
    [InlineData("SuperOpcode_bad")]
    [InlineData("op::OpA and op::OpB")]
    public void UnknownOrAmbiguousHandlersFailClosed(string symbol) =>
        Assert.Throws<InvalidDataException>(() => Tool.NormalizeTrainingHandler(symbol));

    [Theory]
    [InlineData("op::OpMov_Load_Eax", "op::OpCmp_EaxImm", true)]
    [InlineData("op::OpCmp_EaxImm", "op::OpJcc_E_Rel8", true)]
    [InlineData("op::OpCall_Rel", "op::OpMov_Load_Eax", false)]
    [InlineData("op::OpMov_Load_Eax", "op::OpUnknown", false)]
    [InlineData("op::OpMovs_Byte", "op::OpCmp_EaxImm", false)]
    [InlineData("op::OpCmps_Byte", "op::OpCmp_EaxImm", false)]
    public void CandidatePolicyRejectsUnsupportedProtocols(string first, string second, bool allowed) =>
        Assert.Equal(allowed, Tool.TrainingPairAllowed(first, second));

    [Fact]
    public void GenerationIsByteStableAndIndependentOfInputFilename()
    {
        var directory = Path.Combine(Path.GetTempPath(), "podish-superopcode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.json");
            var second = Path.Combine(directory, "second.json");
            const string json = "{\"candidates\":[{\"pair\":[\"op::OpMov_Load_Eax\",\"op::OpCmp_EaxImm\"]}]}";
            File.WriteAllText(first, json); File.WriteAllText(second, json);
            var generated = Tool.GenerateSuperopcodes(first, 1);
            Assert.Equal(generated, Tool.GenerateSuperopcodes(second, 1));
            Assert.DoesNotContain('\r', generated);
            Assert.DoesNotContain(directory, generated);
            File.WriteAllText(first, "{\"candidates\":[]}");
            Assert.Throws<InvalidDataException>(() => Tool.GenerateSuperopcodes(first, 1));
            File.WriteAllText(first, "{\"candidates\":[{\"pair\":[\"op::OpBad();\",\"op::OpCmp_EaxImm\"]}]}");
            Assert.Throws<InvalidDataException>(() => Tool.GenerateSuperopcodes(first, 1));
        }
        finally { Directory.Delete(directory, true); }
    }
}

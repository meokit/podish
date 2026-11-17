using System.Text.Json;
using Xunit;
using Tool = Podish.PerfTools.Program;

namespace Podish.PerfTools.Tests;

public sealed class InstructionCatalogTests
{
    [Fact]
    public void FusionFamiliesComeFromMetadataRatherThanACompiledList()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                schema_version = 1, source_sha256 = Tool.InstructionCatalogSourceHash(),
                families = new Dictionary<string, string[]> { ["op::OpFuture"] = ["op::OpFuture_Reg"] }
            }));
            Assert.Equal(new[] { "op::OpFuture_Reg" }, Tool.LoadInstructionFusionFamilies(file)["op::OpFuture"]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void StaleCatalogAndInvalidCodeReferencesAreRejected()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{"schema_version":1,"source_sha256":"stale","families":{}}""");
            Assert.Throws<InvalidDataException>(() => Tool.LoadInstructionFusionFamilies(file));
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                schema_version = 1, source_sha256 = Tool.InstructionCatalogSourceHash(),
                families = new Dictionary<string, string[]> { ["op::OpFuture"] = ["invalid;reference"] }
            }));
            Assert.Throws<InvalidDataException>(() => Tool.LoadInstructionFusionFamilies(file));
        }
        finally { File.Delete(file); }
    }
}

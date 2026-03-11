namespace Fiberish.SilkFS.Tests;

public class SilkFsOptionsTests
{
    [Fact]
    public void FromSource_ResolvesAbsolutePath()
    {
        var options = SilkFsOptions.FromSource("./silk-store");
        Assert.True(Path.IsPathRooted(options.RootPath));
        Assert.EndsWith("silk-store", options.RootPath, StringComparison.Ordinal);
    }

    [Fact]
    public void FromSource_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => SilkFsOptions.FromSource(""));
    }
}
using Xunit;

namespace Fiberish.X86.Tests.Fex;

// xUnit parallelizes classes (not rows within one class). These are eight distinct default
// collections; no Collection attribute, fixture, static CPU, or custom serial runner is involved.
public sealed class FexShard00Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(0);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard01Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(1);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard02Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(2);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard03Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(3);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard04Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(4);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard05Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(5);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard06Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(6);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

public sealed class FexShard07Tests
{
    public static IEnumerable<object[]> Cases => FexAssets.Cases(7);
    [Theory, MemberData(nameof(Cases))]
    public void PinnedUpstream32Bit(string id) => FexRunner.Execute(id);
}

using System.Text.Json;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public class OciPlatformTests
{
    private const string Index = """
        [
          {"digest":"sha256:windows","platform":{"os":"windows","architecture":"386"}},
          {"digest":"sha256:armv9","platform":{"os":"linux","architecture":"arm64","variant":"v9"}},
          {"digest":"sha256:arm","platform":{"os":"linux","architecture":"arm64","variant":"v8"}},
          {"digest":"sha256:x86","platform":{"os":"linux","architecture":"386"}}
        ]
        """;

    [Fact]
    public void DefaultManifestUsesTheEnabledGuestArchitecture()
    {
        using var index = JsonDocument.Parse(Index);

#if FIBER_GUEST_I386
        Assert.Equal(("sha256:x86", "386"), OciPullService.SelectPlatformManifest(index.RootElement, null));
#else
        Assert.Equal(("sha256:arm", "arm64"), OciPullService.SelectPlatformManifest(index.RootElement, null));
#endif
    }

    [Fact]
    public void ExplicitArm64ManifestAndCacheDependOnTheBuild()
    {
        using var index = JsonDocument.Parse(Index);

#if FIBER_GUEST_AARCH64
        Assert.True(OciPullService.TryParsePlatform("linux/arm64/v8", out var architecture));
        Assert.Equal(("sha256:arm", "arm64"), OciPullService.SelectPlatformManifest(index.RootElement, architecture));
        Assert.Equal("arm64@example_latest", OciPullService.GetImageCacheName("example:latest", "linux/arm64"));
#if FIBER_GUEST_I386
        Assert.Equal("example_latest", OciPullService.GetImageCacheName("example:latest"));
        Assert.Equal("386@example_latest", OciPullService.GetImageCacheName("example:latest", "linux/386"));
#endif
#else
        Assert.False(OciPullService.TryParsePlatform("linux/arm64/v8", out var architecture));
        Assert.Null(OciPullService.SelectPlatformManifest(index.RootElement, architecture).Digest);
#endif
    }
}

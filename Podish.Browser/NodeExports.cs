using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Podish.Core;

namespace Podish.Browser;

[SupportedOSPlatform("browser")]
public static partial class NodeExports
{
    [JSExport]
    public static async Task<int> RunRootfs(byte[] tar, string executable, string[] args)
    {
        using var context = new PodishContext(new PodishContextOptions { WorkDir = "/tmp/podish-node" });
        using var stream = new MemoryStream(tar, 0, tar.Length, writable: false, publiclyVisible: true);
        var result = await context.RunRootfsTarAsync(stream, new PodishRunSpec
        {
            Exe = executable,
            ExeArgs = args,
            AutoRemove = true,
            LogDriver = "none",
            CpuOptions = ["JitMode=off"]
        });
        return result.ExitCode;
    }
}

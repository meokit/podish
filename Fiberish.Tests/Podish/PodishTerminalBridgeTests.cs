using System.Text;
using Fiberish.Core.VFS.TTY;
using Podish.Core;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class PodishTerminalBridgeTests
{
    [Fact]
    public void SetOutputHandler_ReplaysBufferedOutputAndDrainsBacklog()
    {
        var bridge = new PodishTerminalBridge();
        bridge.EmitOutput(TtyEndpointKind.Stdout, Encoding.UTF8.GetBytes("# "));

        TtyEndpointKind? replayKind = null;
        string? replayText = null;
        bridge.SetOutputHandler((kind, data) =>
        {
            replayKind = kind;
            replayText = Encoding.UTF8.GetString(data);
        });

        Assert.Equal(TtyEndpointKind.Stdout, replayKind);
        Assert.Equal("# ", replayText);

        Span<byte> buffer = stackalloc byte[16];
        Assert.Equal(0, bridge.ReadOutput(buffer, 0));
    }
}

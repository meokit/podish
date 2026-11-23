using System.Reflection;
using Podish.Cli.Display;
using Xunit;
using static Podish.Wayland.Tests.WaylandTestHelpers;

namespace Podish.Wayland.Tests;

public sealed class WaylandTextInputTests
{
    [Theory]
    [InlineData(-1, false, -1)]
    [InlineData(2, false, 4)]
    [InlineData(3, false, 8)]
    [InlineData(3, true, 4)]
    [InlineData(4, true, 8)]
    public void SdlEditingOffsetsConvertToUtf8ByteBoundaries(int offset, bool usesUtf16, int byteOffset)
    {
        Assert.Equal(byteOffset, SdlDisplayOutput.ToUtf8Offset("a中😀", offset, usesUtf16));
    }

    [Fact]
    public async Task ImeUpdatesUseEachObjectsCommitCountAndStopAcrossFocusChanges()
    {
        var presenter = new TestScenePresenter();
        var server = new WaylandServer(presenter);
        var sent = new List<WaylandOutgoingMessage>();
        var client = server.CreateClient(message =>
        {
            sent.Add(Clone(message));
            return new ValueTask<int>(message.Buffer.Length);
        });

        await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
        List<GlobalInfo> globals = ParseGlobals(sent, 2);
        await BindAsync(WlCompositorProtocol.InterfaceName, 3);
        await BindAsync(WlSeatProtocol.InterfaceName, 4);
        await BindAsync(ZwpTextInputManagerV3Protocol.InterfaceName, 5);
        await SendRequestAsync(client, 4, 0, writer => writer.WriteNewId(6));
        await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(8));
        foreach (uint input in new uint[] { 9, 10 })
        {
            await SendRequestAsync(client, 5, 1, writer =>
            {
                writer.WriteNewId(input);
                writer.WriteObjectId(4);
            });
        }

        object surface = client.Objects.Require(8);
        var sceneSurfaceId = (ulong)surface.GetType()
            .GetProperty("SceneSurfaceId", BindingFlags.Instance | BindingFlags.Public)!.GetValue(surface)!;
        presenter.SetSurfaceBounds(sceneSurfaceId, new WaylandSurfaceBounds(10, 10, 50, 50));

        await SendTextInputRequestAsync(9, 1);
        await SendTextInputRequestAsync(9, 7);
        await server.HandlePointerMotionAsync(20, 20, 1);
        await server.HandlePointerButtonAsync(0x110, true, 2);
        Assert.Contains(sent, message => DecodeHeader(message).ObjectId == 9 && DecodeHeader(message).Opcode == 0);
        Assert.Contains(sent, message => DecodeHeader(message).ObjectId == 10 && DecodeHeader(message).Opcode == 0);
        sent.Clear();
        await server.HandleTextInputCommitAsync("stale");
        Assert.Empty(sent);

        await SendTextInputRequestAsync(9, 1);
        await SendTextInputRequestAsync(9, 7);
        await SendTextInputRequestAsync(10, 1);
        await SendTextInputRequestAsync(10, 7);
        await server.HandleTextInputPreeditAsync("拼", 0, 3);
        await server.HandleTextInputCommitAsync("拼");
        Assert.Equal(new uint[] { 2, 2 }, DoneSerials(9));
        Assert.Empty(DoneSerials(10));

        await SendTextInputRequestAsync(9, 2);
        await SendTextInputRequestAsync(9, 7);
        await SendTextInputRequestAsync(10, 1);
        await SendTextInputRequestAsync(10, 7);
        await server.HandleTextInputPreeditAsync("写", 3, 3);
        await server.HandleHostTextInputFocusLostAsync();
        Assert.Equal(new uint[] { 2, 2 }, DoneSerials(10));
        Assert.Contains(sent, message => DecodeHeader(message).ObjectId == 10 && DecodeHeader(message).Opcode == 1);

        sent.Clear();
        await server.HandleHostTextInputFocusGainedAsync();
        await server.HandleTextInputCommitAsync("stale after reentry");
        Assert.Empty(DoneSerials(9));
        Assert.Empty(DoneSerials(10));
        await SendTextInputRequestAsync(10, 1);
        await SendTextInputRequestAsync(10, 7);
        await server.HandleTextInputCommitAsync("新");
        Assert.Equal(new uint[] { 3 }, DoneSerials(10));

        async Task BindAsync(string protocol, uint id)
        {
            GlobalInfo global = globals.Single(item => item.Interface == protocol);
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(global.Name);
                writer.WriteString(protocol);
                writer.WriteUInt(global.Version);
                writer.WriteNewId(id);
            });
        }

        Task SendTextInputRequestAsync(uint id, ushort opcode) =>
            SendRequestAsync(client, id, opcode, static _ => { });

        uint[] DoneSerials(uint objectId) => sent
            .Where(message => DecodeHeader(message).ObjectId == objectId && DecodeHeader(message).Opcode == 5)
            .Select(message => new WaylandWireReader(message.Buffer[WaylandMessageHeader.SizeInBytes..]).ReadUInt())
            .ToArray();
    }
}

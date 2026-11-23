using Xunit;
using static Podish.Wayland.Tests.WaylandTestHelpers;

namespace Podish.Wayland.Tests;

public sealed class WaylandSelectionTests
{
    [Fact]
    public async Task BindingSelectionDevicesBeforeKeyboardFocusDoesNotSendEmptySelectionEvents()
    {
        var server = new WaylandServer();
        var events = new List<WaylandOutgoingMessage>();
        var client = server.CreateClient(message =>
        {
            events.Add(Clone(message));
            return ValueTask.FromResult(message.Buffer.Length);
        });
        await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
        var globals = ParseGlobals(events, 2);
        await BindAsync(WlSeatProtocol.InterfaceName, 3);
        await BindAsync(WlDataDeviceManagerProtocol.InterfaceName, 4);
        await BindAsync(ZwpPrimarySelectionDeviceManagerV1Protocol.InterfaceName, 5);

        events.Clear();
        await SendRequestAsync(client, 4, 1, writer =>
        {
            writer.WriteNewId(6);
            writer.WriteObjectId(3);
        });
        await SendRequestAsync(client, 5, 1, writer =>
        {
            writer.WriteNewId(7);
            writer.WriteObjectId(3);
        });
        Assert.Empty(events);
        await server.ShutdownAsync();

        Task BindAsync(string name, uint objectId) => SendRequestAsync(client, 2, 0, writer =>
        {
            writer.WriteUInt(globals.Single(global => global.Interface == name).Name);
            writer.WriteString(name);
            writer.WriteUInt(1);
            writer.WriteNewId(objectId);
        });
    }
}

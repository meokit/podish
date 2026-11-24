using Fiberish.Memory;
using Fiberish.VFS;
using Podish.Cli.Wayland;
using Xunit;
using static Podish.Wayland.Tests.WaylandTestHelpers;

namespace Podish.Wayland.Tests;

public sealed class WaylandShellTests
{
    [Fact]
    public async Task FractionalOutputAndScaledBuffersKeepLogicalWindowBounds()
    {
        using var memory = new MemoryRuntimeContext();
        SuperBlock shm = CreateSharedMemory(memory);
        var commands = new List<WaylandDisplayCommand>();
        using var presenter = new WaylandSdlFramePresenter(new WaylandDesktopOptions(320, 200), commands.Add);
        var server = new WaylandServer(presenter, new(320, 200, 2) { Scale120 = 180 });
        var events = new List<WaylandOutgoingMessage>();
        WaylandClient client = CreateClient(server, events);
        await BindAsync(client, WlOutputProtocol.InterfaceName, 4, 3);
        var mode = LatestEvent(events, 3, 1);
        _ = mode.ReadUInt();
        Assert.Equal(480, mode.ReadInt());
        Assert.Equal(300, mode.ReadInt());
        Assert.Equal(2, LatestEvent(events, 3, 3).ReadInt());

        await BindAsync(client, ZxdgOutputManagerV1Protocol.InterfaceName, 3, 4);
        await SendRequestAsync(client, 4, 1, writer => { writer.WriteNewId(5); writer.WriteObjectId(3); });
        var logical = LatestEvent(events, 5, 1);
        Assert.Equal(320, logical.ReadInt());
        Assert.Equal(200, logical.ReadInt());

        await BindAsync(client, WlCompositorProtocol.InterfaceName, 4, 6);
        await BindAsync(client, WlShmProtocol.InterfaceName, 1, 7);
        await BindAsync(client, WpFractionalScaleManagerV1Protocol.InterfaceName, 1, 8);
        await BindAsync(client, WpViewporterProtocol.InterfaceName, 1, 9);
        await SendRequestAsync(client, 6, 0, writer => writer.WriteNewId(10));
        await SendRequestAsync(client, 8, 1, writer => { writer.WriteNewId(11); writer.WriteObjectId(10); });
        Assert.Equal(180u, LatestEvent(events, 11, 0).ReadUInt());

        await SendRequestAsync(client, 10, 8, writer => writer.WriteInt(2));
        await AttachBufferAsync(shm, client, 7, 10, 12, 160, 100);
        var first = commands.Last(command => command.Kind == WaylandDisplayCommandKind.PresentSurface);
        Assert.Equal(80, first.Bounds.Width);
        Assert.Equal(50, first.Bounds.Height);
        Assert.Equal(160, first.Frame.Width);

        await SendRequestAsync(client, 9, 1, writer => { writer.WriteNewId(14); writer.WriteObjectId(10); });
        await SendRequestAsync(client, 14, 1, writer =>
        {
            writer.WriteInt(128); // x = 0.5 logical pixels
            writer.WriteInt(0);
            writer.WriteInt(64 * 256);
            writer.WriteInt(40 * 256);
        });
        await SendRequestAsync(client, 14, 2, writer => { writer.WriteInt(96); writer.WriteInt(60); });
        await SendRequestAsync(client, 10, 6, static _ => { });
        var cropped = commands.Last(command => command.Kind == WaylandDisplayCommandKind.PresentSurface);
        Assert.Equal(96, cropped.Bounds.Width);
        Assert.Equal(60, cropped.Bounds.Height);
        Assert.Equal(0.5f / 80, cropped.Frame.Mapping!.Value.U, 5);
        Assert.Equal(0.8f, cropped.Frame.Mapping.Value.Ux, 5);
        await server.ShutdownAsync();
    }

    [Fact]
    public async Task LinuxDmabufRejectsDescriptorsThatAreNotVirtualGpuBuffers()
    {
        using var memory = new MemoryRuntimeContext();
        SuperBlock shm = CreateSharedMemory(memory);
        using var presenter = new WaylandSdlFramePresenter(WaylandDesktopOptions.Default, _ => { });
        var server = new WaylandServer(presenter, enableGpuBuffers: true);
        var events = new List<WaylandOutgoingMessage>();
        WaylandClient client = CreateClient(server, events);
        await BindAsync(client, ZwpLinuxDmabufV1Protocol.InterfaceName, 3, 3);
        Assert.Equal(2, events.Count(message => IsEvent(message, 3, 1)));
        await SendRequestAsync(client, 3, 1, writer => writer.WriteNewId(4));

        Inode inode = shm.AllocInode();
        inode.Type = InodeType.File;
        Assert.Equal(0, inode.Truncate(16 * 16 * 4));
        var fd = new LinuxFile(new Dentry("ordinary-file", inode, shm.Root, shm), FileFlags.O_RDWR, null);
        await SendRequestAsync(client, 4, 1, writer =>
        {
            writer.WriteFd(fd);
            writer.WriteUInt(0);
            writer.WriteUInt(0);
            writer.WriteUInt(16 * 4);
            writer.WriteUInt(0x00ff_ffff);
            writer.WriteUInt(uint.MaxValue);
        });
        await SendRequestAsync(client, 4, 2, writer =>
        {
            writer.WriteInt(16);
            writer.WriteInt(16);
            writer.WriteUInt(0x34325258);
            writer.WriteUInt(0);
        });
        Assert.Contains(events, message => IsEvent(message, 4, 1));
        Assert.DoesNotContain(events, message => IsEvent(message, 4, 0));
        await server.ShutdownAsync();
    }

    [Fact]
    public async Task AppIdUpdatesTheIconOnAnAlreadyMappedWindow()
    {
        using var memory = new MemoryRuntimeContext();
        SuperBlock shm = CreateSharedMemory(memory);
        var scene = new TestScenePresenter();
        var icon = new WaylandApplicationIcon(new byte[] { 1 }, IsSvg: false);
        int lookups = 0;
        var server = new WaylandServer(scene, resolveApplicationIcon: appId =>
        {
            lookups++;
            return appId == "org.podish.Editor" ? icon : null;
        });
        var events = new List<WaylandOutgoingMessage>();
        WaylandClient app = CreateClient(server, events);
        await BindAsync(app, WlCompositorProtocol.InterfaceName, 4, 3);
        await BindAsync(app, WlShmProtocol.InterfaceName, 1, 4);
        await BindAsync(app, XdgWmBaseProtocol.InterfaceName, 3, 5);
        await CreateWindowAsync(shm, app, events, 3, 4, 5, 6);
        ulong sceneId = SceneId(app, 6);

        await SendRequestAsync(app, 8, 3, writer => writer.WriteString("org.podish.Editor"));
        Assert.Same(icon, scene.GetDecoration(sceneId).ApplicationIcon);
        await SendRequestAsync(app, 8, 2, writer => writer.WriteString("New document"));
        await SendRequestAsync(app, 8, 3, writer => writer.WriteString("org.podish.Editor"));
        Assert.Same(icon, scene.GetDecoration(sceneId).ApplicationIcon);
        Assert.Equal(1, lookups);
        await SendRequestAsync(app, 8, 3, writer => writer.WriteString("org.podish.Other"));
        Assert.Null(scene.GetDecoration(sceneId).ApplicationIcon);
        await server.ShutdownAsync();
    }

    [Fact]
    public async Task PopupFlipsAboveItsAnchorAndDismissesOnOutsideClick()
    {
        using var memory = new MemoryRuntimeContext();
        SuperBlock shm = CreateSharedMemory(memory);
        var scene = new TestScenePresenter();
        var server = new WaylandServer(scene, new(320, 200));
        var events = new List<WaylandOutgoingMessage>();
        WaylandClient app = CreateClient(server, events);
        await BindAsync(app, WlCompositorProtocol.InterfaceName, 4, 3);
        await BindAsync(app, WlShmProtocol.InterfaceName, 1, 4);
        await BindAsync(app, XdgWmBaseProtocol.InterfaceName, 3, 5);
        await BindAsync(app, WlSeatProtocol.InterfaceName, 7, 6);
        await SendRequestAsync(app, 6, 0, writer => writer.WriteNewId(7));
        await CreateWindowAsync(shm, app, events, 3, 4, 5, 8);
        scene.SetSurfaceBounds(SceneId(app, 8), new(60, 140, 100, 50));

        await server.HandlePointerMotionAsync(80, 180, 10);
        await server.HandlePointerButtonAsync(273, true, 11);
        uint pointerSerial = LatestEvent(events, 7, 3).ReadUInt();
        await server.HandlePointerButtonAsync(273, false, 12);

        await SendRequestAsync(app, 5, 1, writer => writer.WriteNewId(13));
        await SendRequestAsync(app, 13, 1, writer => { writer.WriteInt(70); writer.WriteInt(30); });
        await SendRequestAsync(app, 13, 2, writer =>
        {
            writer.WriteInt(10);
            writer.WriteInt(40);
            writer.WriteInt(10);
            writer.WriteInt(5);
        });
        await SendRequestAsync(app, 13, 3, writer => writer.WriteUInt((uint)XdgPositionerAnchor.Bottom));
        await SendRequestAsync(app, 13, 4, writer => writer.WriteUInt((uint)XdgPositionerGravity.Bottom));
        await SendRequestAsync(app, 13, 5, writer => writer.WriteUInt((uint)XdgPositionerConstraintAdjustment.FlipY));
        await CreateXdgSurfaceAsync(app, 3, 5, 14);
        await SendRequestAsync(app, 15, 2, writer =>
        {
            writer.WriteNewId(16);
            writer.WriteObjectId(9);
            writer.WriteObjectId(13);
        });
        await SendRequestAsync(app, 16, 1, writer =>
        {
            writer.WriteObjectId(6);
            writer.WriteUInt(pointerSerial);
        });
        Assert.DoesNotContain(events, message => IsEvent(message, 16, 0));
        await SendRequestAsync(app, 14, 6, static _ => { });

        WaylandWireReader configured = LatestEvent(events, 16, 0);
        Assert.Equal(-20, configured.ReadInt());
        Assert.Equal(10, configured.ReadInt());
        Assert.Equal(70, configured.ReadInt());
        Assert.Equal(30, configured.ReadInt());
        await SendRequestAsync(app, 15, 4, writer => writer.WriteUInt(LatestEvent(events, 15, 0).ReadUInt()));
        await SendRequestAsync(app, 14, 8, writer => writer.WriteInt(2));
        await AttachBufferAsync(shm, app, 4, 14, 17, 140, 60);
        Assert.True(scene.TryGetSurfaceBounds(SceneId(app, 14), out var bounds));
        Assert.Equal(new WaylandSurfaceBounds(40, 150, 70, 30), bounds);

        await server.HandlePointerMotionAsync(2, 2, 20);
        await server.HandlePointerButtonAsync(272, true, 21);
        Assert.Contains(events, message => IsEvent(message, 16, 1));
        await server.ShutdownAsync();
    }

    [Fact]
    public async Task LayerPanelReservesWorkAreaAndTaskbarControlsAnotherClientsWindow()
    {
        using var memory = new MemoryRuntimeContext();
        SuperBlock shm = CreateSharedMemory(memory);
        var scene = new TestScenePresenter();
        var server = new WaylandServer(scene, new(320, 200));
        var panelEvents = new List<WaylandOutgoingMessage>();
        WaylandClient panel = CreateClient(server, panelEvents);
        await BindAsync(panel, WlCompositorProtocol.InterfaceName, 4, 3);
        await BindAsync(panel, WlShmProtocol.InterfaceName, 1, 4);
        await BindAsync(panel, ZwlrLayerShellV1Protocol.InterfaceName, 4, 5);
        await BindAsync(panel, WlOutputProtocol.InterfaceName, 4, 6);
        await BindAsync(panel, WlSeatProtocol.InterfaceName, 7, 7);
        await SendRequestAsync(panel, 3, 0, writer => writer.WriteNewId(8));
        await SendRequestAsync(panel, 5, 0, writer =>
        {
            writer.WriteNewId(9);
            writer.WriteObjectId(8);
            writer.WriteObjectId(6);
            writer.WriteUInt((uint)ZwlrLayerShellV1Layer.Top);
            writer.WriteString("test-panel");
        });
        await SendRequestAsync(panel, 9, 0, writer => { writer.WriteUInt(0); writer.WriteUInt(24); });
        await SendRequestAsync(panel, 9, 1, writer => writer.WriteUInt((uint)(
            ZwlrLayerSurfaceV1Anchor.Bottom | ZwlrLayerSurfaceV1Anchor.Left | ZwlrLayerSurfaceV1Anchor.Right)));
        await SendRequestAsync(panel, 9, 2, writer => writer.WriteInt(24));
        await SendRequestAsync(panel, 8, 6, static _ => { });
        WaylandWireReader configured = LatestEvent(panelEvents, 9, 0);
        uint serial = configured.ReadUInt();
        Assert.Equal(320u, configured.ReadUInt());
        Assert.Equal(24u, configured.ReadUInt());
        await SendRequestAsync(panel, 9, 6, writer => writer.WriteUInt(serial));
        await SendRequestAsync(panel, 8, 8, writer => writer.WriteInt(2));
        await AttachBufferAsync(shm, panel, 4, 8, 10, 640, 48);
        Assert.True(scene.TryGetSurfaceBounds(SceneId(panel, 8), out var panelBounds));
        Assert.Equal(new WaylandSurfaceBounds(0, 176, 320, 24), panelBounds);

        var appEvents = new List<WaylandOutgoingMessage>();
        WaylandClient app = CreateClient(server, appEvents);
        await BindAsync(app, WlCompositorProtocol.InterfaceName, 4, 3);
        await BindAsync(app, WlShmProtocol.InterfaceName, 1, 4);
        await BindAsync(app, XdgWmBaseProtocol.InterfaceName, 3, 5);
        await CreateWindowAsync(shm, app, appEvents, 3, 4, 5, 6);
        await SendRequestAsync(app, 8, 2, writer => writer.WriteString("Guest App"));

        await BindAsync(panel, ZwlrForeignToplevelManagerV1Protocol.InterfaceName, 3, 12);
        uint handle = LatestEvent(panelEvents, 12, 0).ReadUInt();
        Assert.Equal("Guest App", LatestEvent(panelEvents, handle, 0).ReadString());
        Assert.Equal(6u, LatestEvent(panelEvents, handle, 2).ReadUInt());

        await SendRequestAsync(panel, handle, 0, static _ => { });
        WaylandWireReader maximized = LatestEvent(appEvents, 8, 0);
        Assert.Equal(320, maximized.ReadInt());
        Assert.Equal(176, maximized.ReadInt());
        await SendRequestAsync(panel, handle, 2, static _ => { });
        Assert.Contains((byte)ZwlrForeignToplevelHandleV1State.Minimized, LatestEvent(panelEvents, handle, 4).ReadArray());
        await SendRequestAsync(panel, handle, 4, writer => writer.WriteObjectId(7));
        byte[] restored = LatestEvent(panelEvents, handle, 4).ReadArray();
        Assert.DoesNotContain((byte)ZwlrForeignToplevelHandleV1State.Minimized, restored);
        Assert.Contains((byte)ZwlrForeignToplevelHandleV1State.Activated, restored);
        await SendRequestAsync(panel, handle, 5, static _ => { });
        Assert.Contains(appEvents, message => IsEvent(message, 8, 1));
        await server.ShutdownAsync();
    }

    private static WaylandClient CreateClient(WaylandServer server, List<WaylandOutgoingMessage> events)
    {
        return server.CreateClient(message =>
        {
            events.Add(Clone(message));
            return ValueTask.FromResult(message.Buffer.Length);
        });
    }

    private static async Task BindAsync(WaylandClient client, string name, uint version, uint id)
    {
        if (!client.Objects.TryGet(2, out _))
            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
        uint global = client.Server.Globals.Globals.Single(entry => entry.Interface == name).Name;
        await SendRequestAsync(client, 2, 0, writer =>
        {
            writer.WriteUInt(global);
            writer.WriteString(name);
            writer.WriteUInt(version);
            writer.WriteNewId(id);
        });
    }

    private static async Task CreateWindowAsync(SuperBlock memory, WaylandClient client, List<WaylandOutgoingMessage> events,
        uint compositor, uint shm, uint wm, uint surface)
    {
        await CreateXdgSurfaceAsync(client, compositor, wm, surface);
        await SendRequestAsync(client, surface + 1, 1, writer => writer.WriteNewId(surface + 2));
        uint serial = LatestEvent(events, surface + 1, 0).ReadUInt();
        await SendRequestAsync(client, surface + 1, 4, writer => writer.WriteUInt(serial));
        await AttachBufferAsync(memory, client, shm, surface, surface + 3, 100, 50);
    }

    private static async Task CreateXdgSurfaceAsync(WaylandClient client, uint compositor, uint wm, uint surface)
    {
        await SendRequestAsync(client, compositor, 0, writer => writer.WriteNewId(surface));
        await SendRequestAsync(client, wm, 2, writer =>
        {
            writer.WriteNewId(surface + 1);
            writer.WriteObjectId(surface);
        });
    }

    private static async Task AttachBufferAsync(SuperBlock memory, WaylandClient client, uint shm, uint surface, uint pool, int width, int height)
    {
        int bytes = width * height * 4;
        Inode inode = memory.AllocInode();
        inode.Type = InodeType.File;
        Assert.Equal(0, inode.Truncate(bytes));
        var dentry = new Dentry("wayland-shell", inode, memory.Root, memory);
        var fd = new LinuxFile(dentry, FileFlags.O_RDWR, null);
        await SendRequestAsync(client, shm, 0, writer =>
        {
            writer.WriteNewId(pool);
            writer.WriteFd(fd);
            writer.WriteInt(bytes);
        });
        await SendRequestAsync(client, pool, 0, writer =>
        {
            writer.WriteNewId(pool + 1);
            writer.WriteInt(0);
            writer.WriteInt(width);
            writer.WriteInt(height);
            writer.WriteInt(width * 4);
            writer.WriteUInt((uint)WlShmFormat.Xrgb8888);
        });
        await SendRequestAsync(client, surface, 1, writer =>
        {
            writer.WriteObjectId(pool + 1);
            writer.WriteInt(0);
            writer.WriteInt(0);
        });
        await SendRequestAsync(client, surface, 6, static _ => { });
    }

    private static SuperBlock CreateSharedMemory(MemoryRuntimeContext memory) =>
        new Tmpfs(memoryContext: memory).ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, string.Empty, null);

    private static bool IsEvent(WaylandOutgoingMessage message, uint objectId, ushort opcode)
    {
        WaylandMessageHeader header = DecodeHeader(message);
        return header.ObjectId == objectId && header.Opcode == opcode;
    }

    private static WaylandWireReader LatestEvent(List<WaylandOutgoingMessage> events, uint objectId, ushort opcode) =>
        new(events.Last(message => IsEvent(message, objectId, opcode)).Buffer[WaylandMessageHeader.SizeInBytes..]);

    private static ulong SceneId(WaylandClient client, uint surfaceId)
    {
        WaylandResource surface = client.Objects.Require(surfaceId);
        return (ulong)surface.GetType().GetProperty("SceneSurfaceId")!.GetValue(surface)!;
    }
}

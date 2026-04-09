using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;
using Podish.Cli;
using Xunit;
using static Podish.Wayland.Tests.WaylandTestHelpers;

namespace Podish.Wayland.Tests;

public sealed class WaylandWireTests
{
    [Fact]
    public void WireWriter_RoundTripsHeaderStringArrayAndFd()
    {
        using var env = new TestEnv();
        env.InvokeOnScheduler(() =>
        {
            var fd = env.CreateMemfdLikeFile("wayland-wire", "hello-fd");
            var writer = new WaylandWireWriter();
            writer.WriteString("hello");
            writer.WriteArray([1, 2, 3]);
            writer.WriteFd(fd);

            var outgoing = writer.ToOutgoingMessage(7, 9);
            Assert.Equal(1, outgoing.Fds?.Count);

            var header = WaylandMessageHeader.Decode(outgoing.Buffer);
            Assert.Equal<uint>(7, header.ObjectId);
            Assert.Equal<ushort>(9, header.Opcode);
            Assert.Equal(outgoing.Buffer.Length, header.Size);

            var reader = new WaylandWireReader(outgoing.Buffer[WaylandMessageHeader.SizeInBytes..], outgoing.Fds);
            Assert.Equal("hello", reader.ReadString());
            Assert.Equal([1, 2, 3], reader.ReadArray());
            Assert.Same(fd, reader.ReadFd());
            reader.EnsureExhausted();
            fd.Close();
        });
    }
}

public sealed class WaylandRuntimeTests
{
    [Fact]
    public void Theme_BreezeLight_HasStableDecorationTokens()
    {
        var theme = WaylandUiTheme.BreezeLight;

        Assert.Equal((byte)142, theme.Desktop.Background.R);
        Assert.Equal((byte)158, theme.Desktop.Background.G);
        Assert.Equal((byte)182, theme.Desktop.Background.B);
        Assert.True(theme.Spacing.WindowBorderThickness > 0);
        Assert.True(theme.Spacing.TitlebarHeight > 0);
        Assert.True(theme.Spacing.ButtonSize > 0);
        Assert.True(theme.Spacing.IconStrokeWidth > 0);
        Assert.True(theme.Corners.WindowCornerRadius >= 0);
        Assert.True(theme.WindowDecoration.Buttons.CornerRadius >= 0);
    }

    [Fact]
    public void Theme_Layout_UsesThemeSpacingForWindowAndButtons()
    {
        var theme = WaylandUiTheme.BreezeLight;
        WaylandDecorationSceneState decoration = new(true, true, false, false, "foot");
        WaylandSurfaceBounds content = new(100, 120, 640, 400);

        var window = WaylandDecorationLayout.GetWindowBounds(content, decoration, theme);
        Assert.Equal(content.X - theme.Spacing.WindowBorderThickness, window.X);
        Assert.Equal(content.Y - theme.Spacing.TitlebarHeight, window.Y);
        Assert.Equal(content.Width + theme.Spacing.WindowBorderThickness * 2, window.Width);
        Assert.Equal(content.Height + theme.Spacing.TitlebarHeight + theme.Spacing.WindowBorderThickness,
            window.Height);

        var close = WaylandDecorationLayout.GetCloseButtonBounds(window, theme);
        var maximize = WaylandDecorationLayout.GetMaximizeButtonBounds(window, theme);
        var minimize = WaylandDecorationLayout.GetMinimizeButtonBounds(window, theme);

        Assert.Equal(theme.Spacing.ButtonSize, close.Width);
        Assert.Equal(theme.Spacing.ButtonSize, maximize.Width);
        Assert.Equal(theme.Spacing.ButtonSize, minimize.Width);
        Assert.Equal(theme.Spacing.ButtonGap, maximize.X - (minimize.X + minimize.Width));
        Assert.Equal(theme.Spacing.ButtonGap, close.X - (maximize.X + maximize.Width));
    }

    [Fact]
    public async Task Runtime_CursorShapeGlobal_IsAdvertised()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer();
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);

            Assert.Contains(globals, static g => g.Interface == WpCursorShapeManagerV1Protocol.InterfaceName);
        });
    }

    [Fact]
    public async Task Runtime_CursorShapeGetPointer_DuplicateBindingRaisesProtocolError()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer();
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var seatName = globals.Single(x => x.Interface == WlSeatProtocol.InterfaceName).Name;
            var cursorShapeName = globals.Single(x => x.Interface == WpCursorShapeManagerV1Protocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(seatName);
                writer.WriteString(WlSeatProtocol.InterfaceName);
                writer.WriteUInt(7);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(4));
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(cursorShapeName);
                writer.WriteString(WpCursorShapeManagerV1Protocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });

            await SendRequestAsync(client, 5, 1, writer =>
            {
                writer.WriteNewId(6);
                writer.WriteObjectId(4);
            });

            await Assert.ThrowsAsync<WaylandProtocolException>(async () =>
            {
                await SendRequestAsync(client, 5, 1, writer =>
                {
                    writer.WriteNewId(7);
                    writer.WriteObjectId(4);
                });
            });
        });
    }

    [Fact]
    public async Task Runtime_CursorShape_InvalidSerialIsIgnored()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var presenter = new RecordingFramePresenter();
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(presenter);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var seatName = globals.Single(x => x.Interface == WlSeatProtocol.InterfaceName).Name;
            var cursorShapeName = globals.Single(x => x.Interface == WpCursorShapeManagerV1Protocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(seatName);
                writer.WriteString(WlSeatProtocol.InterfaceName);
                writer.WriteUInt(7);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(4));
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(cursorShapeName);
                writer.WriteString(WpCursorShapeManagerV1Protocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });
            await SendRequestAsync(client, 5, 1, writer =>
            {
                writer.WriteNewId(6);
                writer.WriteObjectId(4);
            });

            await SendRequestAsync(client, 6, 1, writer =>
            {
                writer.WriteUInt(999);
                writer.WriteUInt((uint)WpCursorShapeDeviceV1Shape.Text);
            });

            Assert.Null(presenter.LastSystemCursor);
        });
    }

    [Fact]
    public async Task Runtime_CursorShape_SetShapeAndClientCursorSurface_LastRequestWins()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var presenter = new TestScenePresenter();
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(presenter);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var shmName = globals.Single(x => x.Interface == WlShmProtocol.InterfaceName).Name;
            var seatName = globals.Single(x => x.Interface == WlSeatProtocol.InterfaceName).Name;
            var cursorShapeName = globals.Single(x => x.Interface == WpCursorShapeManagerV1Protocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(shmName);
                writer.WriteString(WlShmProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(seatName);
                writer.WriteString(WlSeatProtocol.InterfaceName);
                writer.WriteUInt(7);
                writer.WriteNewId(5);
            });
            await SendRequestAsync(client, 5, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(cursorShapeName);
                writer.WriteString(WpCursorShapeManagerV1Protocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(7);
            });
            await SendRequestAsync(client, 7, 1, writer =>
            {
                writer.WriteNewId(8);
                writer.WriteObjectId(6);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(100);
            });
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(9));

            // Create a separate surface (120) to be the XDG toplevel for scene hit presence
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(120));
            await SendRequestAsync(client, 100, 2, writer =>
            {
                writer.WriteNewId(101);
                writer.WriteObjectId(120);
            });
            await SendRequestAsync(client, 101, 1, writer => writer.WriteNewId(102));
            var serial = new WaylandWireReader(sent.Last().Buffer[WaylandMessageHeader.SizeInBytes..]).ReadUInt();
            await SendRequestAsync(client, 101, 4, writer => writer.WriteUInt(serial));

            // Commit a buffer to the XDG surface so PresentSurfaceAsync registers its bounds
            var sharedFd = env.CreateMemfdLikeFile("xdg-shm", new string('x', 64));
            await SendRequestAsync(client, 4, 0, writer =>
            {
                writer.WriteNewId(50);
                writer.WriteFd(sharedFd);
                writer.WriteInt(64);
            });
            await SendRequestAsync(client, 50, 0, writer =>
            {
                writer.WriteNewId(51);
                writer.WriteInt(0);
                writer.WriteInt(4);
                writer.WriteInt(4);
                writer.WriteInt(16);
                writer.WriteUInt((uint)WlShmFormat.Argb8888);
            });
            await SendRequestAsync(client, 120, 1, writer =>
            {
                writer.WriteObjectId(51);
                writer.WriteInt(0);
                writer.WriteInt(0);
            });
            await SendRequestAsync(client, 120, 6, static _ => { });
            sharedFd.Close();

            sent.Clear();
            await server.HandlePointerMotionAsync(10, 10, 1);
            var enterSerial = sent
                .Select(message => new DecodedMessage(
                    DecodeHeader(message),
                    message.Buffer[WaylandMessageHeader.SizeInBytes..]))
                .Where(m => m.Header.ObjectId == 6 && m.Header.Opcode == 0)
                .Select(m => new WaylandWireReader(m.Body).ReadUInt())
                .Single();

            await SendRequestAsync(client, 8, 1, writer =>
            {
                writer.WriteUInt(enterSerial);
                writer.WriteUInt((uint)WpCursorShapeDeviceV1Shape.Text);
            });
            Assert.Equal(WaylandSystemCursorShape.Text, presenter.LastSystemCursor);

            var fd = env.CreateMemfdLikeFile("wl-cursor", new string('z', 64));
            await SendRequestAsync(client, 4, 0, writer =>
            {
                writer.WriteNewId(10);
                writer.WriteFd(fd);
                writer.WriteInt(64);
            });
            await SendRequestAsync(client, 10, 0, writer =>
            {
                writer.WriteNewId(11);
                writer.WriteInt(0);
                writer.WriteInt(4);
                writer.WriteInt(4);
                writer.WriteInt(16);
                writer.WriteUInt((uint)WlShmFormat.Argb8888);
            });
            await SendRequestAsync(client, 9, 1, writer =>
            {
                writer.WriteObjectId(11);
                writer.WriteInt(0);
                writer.WriteInt(0);
            });
            await SendRequestAsync(client, 9, 6, static _ => { });
            await SendRequestAsync(client, 6, 0, writer =>
            {
                writer.WriteUInt(enterSerial);
                writer.WriteUInt(9);
                writer.WriteInt(1);
                writer.WriteInt(2);
            });
            Assert.NotNull(presenter.LastCursor);

            await SendRequestAsync(client, 8, 1, writer =>
            {
                writer.WriteUInt(enterSerial);
                writer.WriteUInt((uint)WpCursorShapeDeviceV1Shape.Crosshair);
            });
            Assert.Equal(WaylandSystemCursorShape.Crosshair, presenter.LastSystemCursor);
            fd.Close();
        });
    }

    [Fact]
    public async Task Runtime_MinimalXdgShmFlowCompletes()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer();
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var shmName = globals.Single(x => x.Interface == WlShmProtocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(shmName);
                writer.WriteString(WlShmProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });

            Assert.Equal(3, sent.Count);
            Assert.Equal<uint>(4, DecodeHeader(sent[0]).ObjectId);
            Assert.Equal<uint>(4, DecodeHeader(sent[1]).ObjectId);
            Assert.Equal<uint>(5, DecodeHeader(sent[2]).ObjectId);

            sent.Clear();
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 5, 2, writer =>
            {
                writer.WriteNewId(7);
                writer.WriteObjectId(6);
            });
            await SendRequestAsync(client, 7, 1, writer => writer.WriteNewId(8));

            Assert.Equal(2, sent.Count);
            Assert.Equal<uint>(8, DecodeHeader(sent[0]).ObjectId);
            Assert.Equal<uint>(7, DecodeHeader(sent[1]).ObjectId);
            var configureReader = new WaylandWireReader(sent[1].Buffer[WaylandMessageHeader.SizeInBytes..]);
            var serial = configureReader.ReadUInt();
            configureReader.EnsureExhausted();

            await SendRequestAsync(client, 7, 4, writer => writer.WriteUInt(serial));

            var fd = env.CreateMemfdLikeFile("wl-shm", new string('a', 128));
            await SendRequestAsync(client, 4, 0, writer =>
            {
                writer.WriteNewId(9);
                writer.WriteFd(fd);
                writer.WriteInt(128);
            });
            await SendRequestAsync(client, 9, 0, writer =>
            {
                writer.WriteNewId(10);
                writer.WriteInt(0);
                writer.WriteInt(4);
                writer.WriteInt(4);
                writer.WriteInt(16);
                writer.WriteUInt((uint)WlShmFormat.Argb8888);
            });
            await SendRequestAsync(client, 6, 3, writer => writer.WriteNewId(11));
            await SendRequestAsync(client, 6, 1, writer =>
            {
                writer.WriteObjectId(10);
                writer.WriteInt(0);
                writer.WriteInt(0);
            });

            sent.Clear();
            await SendRequestAsync(client, 6, 6, static _ => { });
            Assert.Equal(2, sent.Count);
            Assert.Equal<uint>(8, DecodeHeader(sent[0]).ObjectId);
            Assert.Equal<uint>(7, DecodeHeader(sent[1]).ObjectId);

            sent.Clear();
            await server.HandlePresentationTickAsync(1234);
            Assert.Equal(2, sent.Count);
            Assert.Equal<uint>(11, DecodeHeader(sent[0]).ObjectId);
            Assert.Equal<uint>(1, DecodeHeader(sent[1]).ObjectId);
            fd.Close();
        });
    }

    [Fact]
    public async Task Runtime_BufferRelease_IsDeferredUntilDisplayConsumesLease()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var presenter = new RecordingFramePresenter();
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(presenter);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var shmName = globals.Single(x => x.Interface == WlShmProtocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(shmName);
                writer.WriteString(WlShmProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });

            sent.Clear();
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 5, 2, writer =>
            {
                writer.WriteNewId(7);
                writer.WriteObjectId(6);
            });
            await SendRequestAsync(client, 7, 1, writer => writer.WriteNewId(8));
            var serial = new WaylandWireReader(sent[1].Buffer[WaylandMessageHeader.SizeInBytes..]).ReadUInt();
            await SendRequestAsync(client, 7, 4, writer => writer.WriteUInt(serial));

            var fd = env.CreateMemfdLikeFile("wl-shm-release", new string('c', 128));
            await SendRequestAsync(client, 4, 0, writer =>
            {
                writer.WriteNewId(9);
                writer.WriteFd(fd);
                writer.WriteInt(128);
            });
            await SendRequestAsync(client, 9, 0, writer =>
            {
                writer.WriteNewId(10);
                writer.WriteInt(0);
                writer.WriteInt(4);
                writer.WriteInt(4);
                writer.WriteInt(16);
                writer.WriteUInt((uint)WlShmFormat.Argb8888);
            });

            sent.Clear();
            await SendRequestAsync(client, 6, 1, writer =>
            {
                writer.WriteObjectId(10);
                writer.WriteInt(0);
                writer.WriteInt(0);
            });
            await SendRequestAsync(client, 6, 6, static _ => { });

            Assert.NotNull(presenter.LastFrame);
            Assert.DoesNotContain(sent, message => DecodeHeader(message).ObjectId == 10);

            sent.Clear();
            await server.HandleBufferConsumedAsync(presenter.LastFrame!.Value.LeaseToken);
            Assert.Single(sent);
            Assert.Equal<uint>(10, DecodeHeader(sent[0]).ObjectId);
            fd.Close();
        });
    }

    [Fact]
    public async Task Runtime_Subcompositor_AssignsSubsurfaceRoleAndRejectsRoleReuse()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(new TestScenePresenter());
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var subcompositorName = globals.Single(x => x.Interface == WlSubcompositorProtocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;

            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(subcompositorName);
                writer.WriteString(WlSubcompositorProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });

            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(7));
            await SendRequestAsync(client, 4, 1, writer =>
            {
                writer.WriteNewId(8);
                writer.WriteObjectId(7);
                writer.WriteObjectId(6);
            });

            object childSurface = client.Objects.Require(7);
            var subsurface = childSurface.GetType()
                .GetProperty("Subsurface", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(childSurface);
            Assert.NotNull(subsurface);

            var roleEx = await Assert.ThrowsAsync<WaylandProtocolException>(async () =>
                await SendRequestAsync(client, 5, 2, writer =>
                {
                    writer.WriteNewId(9);
                    writer.WriteObjectId(7);
                }));
            Assert.Contains("already has a role", roleEx.Message);
        });
    }

    [Fact]
    public async Task Runtime_XdgDecoration_CreatesResourceAndDefaultsToServerSide()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var presenter = new TestScenePresenter();
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(presenter);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;
            var decorationName = globals.Single(x => x.Interface == ZxdgDecorationManagerV1Protocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(decorationName);
                writer.WriteString(ZxdgDecorationManagerV1Protocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });

            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 4, 2, writer =>
            {
                writer.WriteNewId(7);
                writer.WriteObjectId(6);
            });
            await SendRequestAsync(client, 7, 1, writer => writer.WriteNewId(8));

            sent.Clear();
            await SendRequestAsync(client, 5, 1, writer =>
            {
                writer.WriteNewId(9);
                writer.WriteObjectId(8);
            });

            Assert.Contains(sent, message => DecodeHeader(message).ObjectId == 9 && DecodeHeader(message).Opcode == 0);
            Assert.Contains(sent, message => DecodeHeader(message).ObjectId == 8 && DecodeHeader(message).Opcode == 0);
        });
    }

    [Fact]
    public async Task Runtime_XdgDecoration_RejectsDuplicateDecorationObject()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(new TestScenePresenter());
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;
            var decorationName = globals.Single(x => x.Interface == ZxdgDecorationManagerV1Protocol.InterfaceName).Name;

            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(decorationName);
                writer.WriteString(ZxdgDecorationManagerV1Protocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            });
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 4, 2, writer =>
            {
                writer.WriteNewId(7);
                writer.WriteObjectId(6);
            });
            await SendRequestAsync(client, 7, 1, writer => writer.WriteNewId(8));
            await SendRequestAsync(client, 5, 1, writer =>
            {
                writer.WriteNewId(9);
                writer.WriteObjectId(8);
            });

            var ex = await Assert.ThrowsAsync<WaylandProtocolException>(async () =>
                await SendRequestAsync(client, 5, 1, writer =>
                {
                    writer.WriteNewId(10);
                    writer.WriteObjectId(8);
                }));
            Assert.Contains("decoration object", ex.Message);
        });
    }

    [Fact]
    public async Task Runtime_SubsurfaceDestroy_RemainsValidAfterParentSurfaceDestroy()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(new TestScenePresenter());
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var subcompositorName = globals.Single(x => x.Interface == WlSubcompositorProtocol.InterfaceName).Name;

            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(subcompositorName);
                writer.WriteString(WlSubcompositorProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });

            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(10));
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(11));
            await SendRequestAsync(client, 4, 1, writer =>
            {
                writer.WriteNewId(12);
                writer.WriteObjectId(11);
                writer.WriteObjectId(10);
            });

            await SendRequestAsync(client, 10, 0, _ => { });
            await SendRequestAsync(client, 12, 0, _ => { });

            Assert.Throws<WaylandProtocolException>(() => client.Objects.Require(10));
            Assert.Throws<WaylandProtocolException>(() => client.Objects.Require(12));
        });
    }

    [Fact]
    public async Task Runtime_ClickFocus_SendsKeyboardEnterOnButtonPress()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var presenter = new TestScenePresenter();
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(presenter);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var seatName = globals.Single(x => x.Interface == WlSeatProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(seatName);
                writer.WriteString(WlSeatProtocol.InterfaceName);
                writer.WriteUInt(7);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 4, 0, writer => writer.WriteNewId(5));
            await SendRequestAsync(client, 4, 1, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(7));

            object surface = client.Objects.Require(7);
            var sceneSurfaceId = (ulong)(surface.GetType().GetProperty("SceneSurfaceId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(surface) ?? 0UL);
            presenter.SetSurfaceBounds(sceneSurfaceId, new WaylandSurfaceBounds(100, 100, 300, 300));

            sent.Clear();
            await server.HandlePointerMotionAsync(120, 130, 1);
            Assert.DoesNotContain(sent,
                message => DecodeHeader(message).ObjectId == 6 && DecodeHeader(message).Opcode == 1);

            await server.HandlePointerButtonAsync(0x110, true, 2);
            Assert.Contains(sent, message => DecodeHeader(message).ObjectId == 6 && DecodeHeader(message).Opcode == 1);
        });
    }

    [Fact]
    public async Task Runtime_KeyboardModifiers_TracksShiftAndCapsLock()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var presenter = new TestScenePresenter();
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer(presenter);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var seatName = globals.Single(x => x.Interface == WlSeatProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(seatName);
                writer.WriteString(WlSeatProtocol.InterfaceName);
                writer.WriteUInt(7);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 4, 1, writer => writer.WriteNewId(5));
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));

            object surface = client.Objects.Require(6);
            var sceneSurfaceId = (ulong)(surface.GetType().GetProperty("SceneSurfaceId",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(surface) ?? 0UL);
            presenter.SetSurfaceBounds(sceneSurfaceId, new WaylandSurfaceBounds(100, 100, 300, 300));

            await server.HandlePointerMotionAsync(120, 130, 1);
            await server.HandlePointerButtonAsync(0x110, true, 2);

            sent.Clear();
            await server.HandleKeyboardKeyAsync(42, true, 3);
            var shiftModifiers = Assert.Single(sent.Where(message =>
                DecodeHeader(message).ObjectId == 5 && DecodeHeader(message).Opcode == 4));
            var shiftReader = new WaylandWireReader(shiftModifiers.Buffer[WaylandMessageHeader.SizeInBytes..]);
            _ = shiftReader.ReadUInt();
            var shiftDepressed = shiftReader.ReadUInt();
            _ = shiftReader.ReadUInt();
            var shiftLocked = shiftReader.ReadUInt();
            Assert.Equal<uint>(1, shiftDepressed);
            Assert.Equal<uint>(0, shiftLocked);

            sent.Clear();
            await server.HandleKeyboardKeyAsync(58, true, 4);
            var capsModifiers = Assert.Single(sent.Where(message =>
                DecodeHeader(message).ObjectId == 5 && DecodeHeader(message).Opcode == 4));
            var capsReader = new WaylandWireReader(capsModifiers.Buffer[WaylandMessageHeader.SizeInBytes..]);
            _ = capsReader.ReadUInt();
            var capsDepressed = capsReader.ReadUInt();
            _ = capsReader.ReadUInt();
            var capsLocked = capsReader.ReadUInt();
            Assert.Equal<uint>(1, capsDepressed);
            Assert.Equal<uint>(2, capsLocked);

            sent.Clear();
            await server.HandleKeyboardKeyAsync(42, false, 5);
            var shiftReleaseModifiers = Assert.Single(sent.Where(message =>
                DecodeHeader(message).ObjectId == 5 && DecodeHeader(message).Opcode == 4));
            var shiftReleaseReader =
                new WaylandWireReader(shiftReleaseModifiers.Buffer[WaylandMessageHeader.SizeInBytes..]);
            _ = shiftReleaseReader.ReadUInt();
            var shiftReleaseDepressed = shiftReleaseReader.ReadUInt();
            _ = shiftReleaseReader.ReadUInt();
            var shiftReleaseLocked = shiftReleaseReader.ReadUInt();
            Assert.Equal<uint>(0, shiftReleaseDepressed);
            Assert.Equal<uint>(2, shiftReleaseLocked);
        });
    }

    [Fact]
    public async Task Runtime_Keymap_IsGeneratedWithoutHostXkbDependency()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(() =>
        {
            var server = new WaylandServer();
            var keymap = server.GetType()
                .GetProperty("KeyboardKeymap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(server)!;
            var size = (uint)(keymap.GetType()
                .GetProperty("Size", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(keymap) ?? 0U);
            var file = (LinuxFile)keymap.GetType()
                .GetMethod("OpenReadOnly", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(keymap, [])!;

            var bytes = new byte[size];
            var read = file.OpenedInode!.ReadToHost(null, file, bytes, 0);
            Assert.Equal((int)size, read);

            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            Assert.Contains("xkb_keymap {", text);
            Assert.Contains("key <LFSH> { [ Shift_L ] };", text);
            Assert.Contains("modifier_map Shift { <LFSH>, <RTSH> };", text);
            Assert.Contains("name[group1] = \"Podish US\";", text);
            return ValueTask.CompletedTask;
        });
    }

    [Fact]
    public async Task Runtime_AckConfigure_AcceptsOlderOutstandingSerial()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer();
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var xdgName = globals.Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(xdgName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            });

            sent.Clear();
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(5));
            await SendRequestAsync(client, 4, 2, writer =>
            {
                writer.WriteNewId(6);
                writer.WriteObjectId(5);
            });
            await SendRequestAsync(client, 6, 1, writer => writer.WriteNewId(7));

            Assert.Equal(2, sent.Count);
            var firstSerial = new WaylandWireReader(sent[1].Buffer[WaylandMessageHeader.SizeInBytes..]).ReadUInt();

            object xdgSurface = client.Objects.Require(6);
            var toplevel = xdgSurface.GetType().GetProperty("Toplevel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(xdgSurface)!;
            var sendConfigureAsync = toplevel.GetType()
                .GetMethod("SendConfigureAsync", [typeof(int), typeof(int), typeof(bool)])!;

            sent.Clear();
            await (ValueTask)sendConfigureAsync.Invoke(toplevel, [320, 240, true])!;

            Assert.Equal(2, sent.Count);
            Assert.Equal<uint>(7, DecodeHeader(sent[0]).ObjectId);
            Assert.Equal<uint>(6, DecodeHeader(sent[1]).ObjectId);
            var secondSerial = new WaylandWireReader(sent[1].Buffer[WaylandMessageHeader.SizeInBytes..]).ReadUInt();
            Assert.NotEqual(firstSerial, secondSerial);

            await SendRequestAsync(client, 6, 4, writer => writer.WriteUInt(firstSerial));

            var ackedSerial = (uint)(xdgSurface.GetType().GetProperty("AckedConfigureSerial",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(xdgSurface) ?? 0U);
            var pendingSerial = (uint)(xdgSurface.GetType().GetProperty("PendingConfigureSerial",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(xdgSurface) ?? 0U);

            Assert.Equal(firstSerial, ackedSerial);
            Assert.Equal(secondSerial, pendingSerial);
        });
    }

    [Fact]
    public async Task Runtime_SetCursor_CanReuseSameCursorSurfaceWithoutChangingRole()
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var server = new WaylandServer();
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            var compositorName = globals.Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var seatName = globals.Single(x => x.Interface == WlSeatProtocol.InterfaceName).Name;

            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            });
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(seatName);
                writer.WriteString(WlSeatProtocol.InterfaceName);
                writer.WriteUInt(7);
                writer.WriteNewId(4);
            });
            await SendRequestAsync(client, 4, 0, writer => writer.WriteNewId(5));
            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));

            await SendRequestAsync(client, 5, 0, writer =>
            {
                writer.WriteUInt(1);
                writer.WriteObjectId(6);
                writer.WriteInt(4);
                writer.WriteInt(5);
            });
            await SendRequestAsync(client, 5, 0, writer =>
            {
                writer.WriteUInt(2);
                writer.WriteObjectId(6);
                writer.WriteInt(8);
                writer.WriteInt(9);
            });

            object surface = client.Objects.Require(6);
            var isCursorRole = (bool)(surface.GetType().GetProperty("IsCursorRole",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(surface) ?? false);
            Assert.True(isCursorRole);
        });
    }
}

public sealed class WaylandVirtualDaemonTests
{
    [Fact]
    public async Task VirtualDaemon_RunsMinimalWaylandFlowWithMemfd()
    {
        using var env = new TestEnv();
        var step = "before invoke";

        await env.InvokeOnSchedulerAsync(async () =>
        {
            step = "spawn daemon";
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddSimpleConsole(options => options.SingleLine = true);
            });
            env.Registry.Spawn(new PodishWaylandVirtualDaemon("/virt-wayland.sock", 0, loggerFactory,
                enableHostDisplay: false));

            step = "connect client";
            using var clientSocket = await env.CreateUnixClientAsync("/virt-wayland.sock");

            step = "get registry";
            await clientSocket.SendAsync(MakeRequest(1, 1, writer => writer.WriteNewId(2)).Buffer);
            var globals = await clientSocket.ReceiveMessagesAsync(11);
            var compositorName = globals.Select(ParseGlobal)
                .Single(x => x.Interface == WlCompositorProtocol.InterfaceName).Name;
            var shmName = globals.Select(ParseGlobal).Single(x => x.Interface == WlShmProtocol.InterfaceName).Name;
            var wmBaseName = globals.Select(ParseGlobal).Single(x => x.Interface == XdgWmBaseProtocol.InterfaceName)
                .Name;

            step = "bind globals";
            await clientSocket.SendAsync(MakeRequest(2, 0, writer =>
            {
                writer.WriteUInt(compositorName);
                writer.WriteString(WlCompositorProtocol.InterfaceName);
                writer.WriteUInt(4);
                writer.WriteNewId(3);
            }).Buffer);
            await clientSocket.SendAsync(MakeRequest(2, 0, writer =>
            {
                writer.WriteUInt(shmName);
                writer.WriteString(WlShmProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(4);
            }).Buffer);
            await clientSocket.SendAsync(MakeRequest(2, 0, writer =>
            {
                writer.WriteUInt(wmBaseName);
                writer.WriteString(XdgWmBaseProtocol.InterfaceName);
                writer.WriteUInt(1);
                writer.WriteNewId(5);
            }).Buffer);

            var bindEvents = await clientSocket.ReceiveMessagesAsync(3);
            Assert.Equal<uint>(4, bindEvents[0].Header.ObjectId);
            Assert.Equal<uint>(4, bindEvents[1].Header.ObjectId);
            Assert.Equal<uint>(5, bindEvents[2].Header.ObjectId);

            step = "create surface";
            await clientSocket.SendAsync(MakeRequest(3, 0, writer => writer.WriteNewId(6)).Buffer);
            await clientSocket.SendAsync(MakeRequest(5, 2, writer =>
            {
                writer.WriteNewId(7);
                writer.WriteObjectId(6);
            }).Buffer);
            await clientSocket.SendAsync(MakeRequest(7, 1, writer => writer.WriteNewId(8)).Buffer);
            var configureMessages = await clientSocket.ReceiveMessagesAsync(2);
            var serial = new WaylandWireReader(configureMessages[1].Body).ReadUInt();
            await clientSocket.SendAsync(MakeRequest(7, 4, writer => writer.WriteUInt(serial)).Buffer);

            step = "shm buffer";
            var fd = env.CreateMemfdLikeFile("virt-wayland-buffer", new string('b', 128));
            var createPool = MakeRequest(4, 0, writer =>
            {
                writer.WriteNewId(9);
                writer.WriteFd(fd);
                writer.WriteInt(128);
            });
            await clientSocket.SendAsync(createPool.Buffer, createPool.Fds);
            await clientSocket.SendAsync(MakeRequest(9, 0, writer =>
            {
                writer.WriteNewId(10);
                writer.WriteInt(0);
                writer.WriteInt(4);
                writer.WriteInt(4);
                writer.WriteInt(16);
                writer.WriteUInt((uint)WlShmFormat.Argb8888);
            }).Buffer);
            await clientSocket.SendAsync(MakeRequest(6, 3, writer => writer.WriteNewId(11)).Buffer);
            await clientSocket.SendAsync(MakeRequest(6, 1, writer =>
            {
                writer.WriteObjectId(10);
                writer.WriteInt(0);
                writer.WriteInt(0);
            }).Buffer);
            await clientSocket.SendAsync(MakeRequest(6, 6, static _ => { }).Buffer);

            step = "frame callback deferred";
            fd.Close();
        }, () => step);
    }
}

internal sealed record GlobalInfo(uint Name, string Interface, uint Version);

internal sealed record DecodedMessage(WaylandMessageHeader Header, byte[] Body);

internal static class WaylandTestHelpers
{
    public static WaylandOutgoingMessage MakeRequest(uint objectId, ushort opcode, Action<WaylandWireWriter> write)
    {
        var writer = new WaylandWireWriter();
        write(writer);
        return writer.ToOutgoingMessage(objectId, opcode);
    }

    public static WaylandMessageHeader DecodeHeader(WaylandOutgoingMessage message)
    {
        return WaylandMessageHeader.Decode(message.Buffer);
    }

    public static List<GlobalInfo> ParseGlobals(List<WaylandOutgoingMessage> sent, uint objectId)
    {
        return sent
            .Select(message =>
                new DecodedMessage(DecodeHeader(message), message.Buffer[WaylandMessageHeader.SizeInBytes..]))
            .Where(decoded => decoded.Header.ObjectId == objectId)
            .Select(ParseGlobal)
            .ToList();
    }

    public static GlobalInfo ParseGlobal(DecodedMessage message)
    {
        var reader = new WaylandWireReader(message.Body);
        var name = reader.ReadUInt();
        var iface = reader.ReadString() ?? string.Empty;
        var version = reader.ReadUInt();
        reader.EnsureExhausted();
        return new GlobalInfo(name, iface, version);
    }

    public static WaylandOutgoingMessage Clone(WaylandOutgoingMessage message)
    {
        return new WaylandOutgoingMessage(message.Buffer.ToArray(), message.Fds?.ToArray());
    }

    public static async Task SendRequestAsync(WaylandClient client, uint objectId, ushort opcode,
        Action<WaylandWireWriter> write)
    {
        var request = MakeRequest(objectId, opcode, write);
        var header = WaylandMessageHeader.Decode(request.Buffer);
        await client.ProcessMessageAsync(new WaylandIncomingMessage(header,
            request.Buffer[WaylandMessageHeader.SizeInBytes..], request.Fds ?? Array.Empty<LinuxFile>()));
    }
}

internal sealed class RecordingFramePresenter : IWaylandFramePresenter, IWaylandCursorPresenter
{
    public WaylandShmFrame? LastFrame { get; private set; }
    public WaylandCursorFrame? LastCursor { get; private set; }
    public WaylandSystemCursorShape? LastSystemCursor { get; private set; }

    public ValueTask UpdateCursorAsync(ulong sceneSurfaceId, WaylandCursorFrame? cursor,
        CancellationToken cancellationToken = default)
    {
        LastCursor = cursor;
        if (cursor != null)
            LastSystemCursor = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateSystemCursorAsync(WaylandSystemCursorShape? shape,
        CancellationToken cancellationToken = default)
    {
        LastSystemCursor = shape;
        if (shape != null)
            LastCursor = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame,
        CancellationToken cancellationToken = default)
    {
        LastFrame = frame;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestScenePresenter : IWaylandFramePresenter, IWaylandSceneView, IWaylandDesktopSceneController,
    IWaylandCursorPresenter
{
    private readonly Dictionary<ulong, WaylandSurfaceBounds> _bounds = [];
    private readonly Dictionary<ulong, WaylandDecorationSceneState> _decorations = [];
    private readonly HashSet<ulong> _hidden = [];
    private readonly WaylandUiTheme _theme = WaylandUiTheme.BreezeLight;

    public WaylandCursorFrame? LastCursor { get; private set; }
    public WaylandSystemCursorShape? LastSystemCursor { get; private set; }

    public ValueTask UpdateCursorAsync(ulong sceneSurfaceId, WaylandCursorFrame? cursor,
        CancellationToken cancellationToken = default)
    {
        LastCursor = cursor;
        if (cursor != null)
            LastSystemCursor = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateSystemCursorAsync(WaylandSystemCursorShape? shape,
        CancellationToken cancellationToken = default)
    {
        LastSystemCursor = shape;
        if (shape != null)
            LastCursor = null;
        return ValueTask.CompletedTask;
    }

    public void ResizeDesktop(int width, int height)
    {
    }

    public void RaiseSurface(ulong sceneSurfaceId)
    {
    }

    public void SetSurfaceBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        _bounds[sceneSurfaceId] = bounds;
    }

    public void SetWindowBounds(ulong sceneSurfaceId, WaylandSurfaceBounds bounds)
    {
        var decoration = _decorations.TryGetValue(sceneSurfaceId, out var state)
            ? state
            : default;
        _bounds[sceneSurfaceId] = WaylandDecorationLayout.GetContentBoundsFromWindowBounds(bounds, decoration, _theme);
    }

    public void SetSurfaceDecoration(ulong sceneSurfaceId, WaylandDecorationSceneState decoration)
    {
        _decorations[sceneSurfaceId] = decoration;
    }

    public void SetSurfaceHidden(ulong sceneSurfaceId, bool hidden)
    {
        if (hidden)
            _hidden.Add(sceneSurfaceId);
        else
            _hidden.Remove(sceneSurfaceId);
    }

    public ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame,
        CancellationToken cancellationToken = default)
    {
        // Auto-register default bounds when a surface first appears so pointer hit-tests can find it
        if (frame != null)
            _bounds.TryAdd(sceneSurfaceId, new WaylandSurfaceBounds(0, 0, 1024, 768));
        return ValueTask.CompletedTask;
    }

    public bool TryGetSurfaceAt(int desktopX, int desktopY, out WaylandSurfaceHit hit)
    {
        if (TryGetSceneHitAt(desktopX, desktopY, out var sceneHit))
        {
            hit = new WaylandSurfaceHit(sceneHit.SceneSurfaceId, sceneHit.SurfaceX, sceneHit.SurfaceY);
            return true;
        }

        hit = default;
        return false;
    }

    public bool TryGetSceneHitAt(int desktopX, int desktopY, out WaylandSceneHit hit)
    {
        foreach (var (sceneSurfaceId, bounds) in _bounds)
        {
            if (_hidden.Contains(sceneSurfaceId))
                continue;

            var decoration = _decorations.TryGetValue(sceneSurfaceId, out var state)
                ? state
                : default;
            var windowBounds = WaylandDecorationLayout.GetWindowBounds(bounds, decoration, _theme);
            if (desktopX < windowBounds.X || desktopY < windowBounds.Y ||
                desktopX >= windowBounds.X + windowBounds.Width || desktopY >= windowBounds.Y + windowBounds.Height)
                continue;

            if (desktopX >= bounds.X && desktopY >= bounds.Y && desktopX < bounds.X + bounds.Width &&
                desktopY < bounds.Y + bounds.Height)
            {
                hit = new WaylandSceneHit(sceneSurfaceId, WaylandSceneHitKind.Surface, desktopX - bounds.X,
                    desktopY - bounds.Y);
                return true;
            }

            hit = new WaylandSceneHit(sceneSurfaceId, WaylandSceneHitKind.Titlebar, desktopX - bounds.X,
                desktopY - bounds.Y);
            return true;
        }

        hit = default;
        return false;
    }

    public bool TryGetSurfaceBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        return _bounds.TryGetValue(sceneSurfaceId, out bounds);
    }

    public bool TryGetWindowBounds(ulong sceneSurfaceId, out WaylandSurfaceBounds bounds)
    {
        if (_bounds.TryGetValue(sceneSurfaceId, out var contentBounds))
        {
            var decoration = _decorations.TryGetValue(sceneSurfaceId, out var state)
                ? state
                : default;
            bounds = WaylandDecorationLayout.GetWindowBounds(contentBounds, decoration, _theme);
            return true;
        }

        bounds = default;
        return false;
    }
}

internal sealed class WaylandSocketClient : IDisposable
{
    private readonly LinuxFile _file;
    private readonly List<byte> _pending = [];
    private readonly byte[] _scratch = new byte[4096];
    private readonly UnixSocketInode _socket;
    private readonly FiberTask _task;

    public WaylandSocketClient(UnixSocketInode socket, LinuxFile file, FiberTask task)
    {
        _socket = socket;
        _file = file;
        _task = task;
    }

    public void Dispose()
    {
        _file.Close();
    }

    public async Task SendAsync(byte[] buffer, IReadOnlyList<LinuxFile>? fds = null)
    {
        var rc = await _socket.SendMsgAsync(_file, _task, buffer, fds?.ToList(), 0, null);
        Assert.Equal(buffer.Length, rc);
    }

    public async Task<List<DecodedMessage>> ReceiveMessagesAsync(int count)
    {
        var messages = new List<DecodedMessage>();
        while (messages.Count < count)
        {
            var read = await _socket.RecvAsync(_file, _task, _scratch, 0, _scratch.Length);
            Assert.True(read > 0);
            _pending.AddRange(_scratch[..read]);

            while (_pending.Count >= WaylandMessageHeader.SizeInBytes)
            {
                var header = WaylandMessageHeader.Decode(CollectionsMarshal.AsSpan(_pending));
                if (_pending.Count < header.Size)
                    break;
                var packet = _pending[..header.Size].ToArray();
                _pending.RemoveRange(0, header.Size);
                messages.Add(new DecodedMessage(header, packet[WaylandMessageHeader.SizeInBytes..]));
                if (messages.Count == count)
                    break;
            }
        }

        return messages;
    }
}

internal sealed class TestEnv : IDisposable
{
    private static readonly FieldInfo OwnerThreadIdField =
        typeof(KernelScheduler).GetField("_ownerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly ClientTaskHandle _anchor;
    private readonly List<ClientTaskHandle> _clients = [];
    private readonly Thread _schedulerThread;
    private Exception? _schedulerFailure;

    public TestEnv()
    {
        Runtime = KernelRuntime.BootstrapBare(false);
        Scheduler = new KernelScheduler();

        var rootFs = new Tmpfs();
        var rootSb = rootFs.ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null);
        Runtime.Syscalls.MountRoot(rootSb, new SyscallManager.RootMountOptions
        {
            Source = "tmpfs",
            FsType = "tmpfs",
            Options = "rw"
        });

        Registry = new VirtualDaemonRegistry(Runtime.Syscalls, Scheduler);
        _anchor = CreateClientTaskCore("scheduler-anchor", false);
        _anchor.Task.Status = FiberTaskStatus.Waiting;
        _schedulerThread = new Thread(() =>
        {
            try
            {
                ResetSchedulerThreadBinding();
                Scheduler.Running = true;
                Scheduler.Run();
            }
            catch (Exception ex)
            {
                _schedulerFailure = ex;
            }
            finally
            {
                ResetSchedulerThreadBinding();
            }
        })
        {
            IsBackground = true,
            Name = "WaylandTests.KernelScheduler"
        };
        _schedulerThread.Start();
        Assert.True(WaitForSchedulerReady(TimeSpan.FromSeconds(5)));
    }

    public KernelRuntime Runtime { get; }
    public KernelScheduler Scheduler { get; }
    public VirtualDaemonRegistry Registry { get; }

    public void Dispose()
    {
        Scheduler.Running = false;
        Scheduler.WakeUp();
        Assert.True(_schedulerThread.Join(TimeSpan.FromSeconds(5)));

        foreach (var client in _clients)
        {
            client.Syscalls.Close();
            client.Engine.Dispose();
        }

        _anchor.Syscalls.Close();
        _anchor.Engine.Dispose();
        Runtime.Syscalls.Close();
        Runtime.Engine.Dispose();
    }

    public void InvokeOnScheduler(Action action)
    {
        InvokeOnSchedulerAsync(() =>
        {
            action();
            return ValueTask.CompletedTask;
        }).GetAwaiter().GetResult();
    }

    public async Task InvokeOnSchedulerAsync(Func<ValueTask> action, Func<string>? stepProvider = null)
    {
        await InvokeOnSchedulerAsync(async () =>
        {
            await action();
            return 0;
        }, stepProvider);
    }

    public async Task<T> InvokeOnSchedulerAsync<T>(Func<ValueTask<T>> action, Func<string>? stepProvider = null)
    {
        if (_schedulerFailure != null)
            throw new InvalidOperationException("Wayland test scheduler thread failed.", _schedulerFailure);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Scheduler.ScheduleFromAnyThread(() => StartScheduledAction(action, tcs));

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) when (_schedulerFailure != null)
        {
            throw new InvalidOperationException("Wayland test scheduler thread failed.", _schedulerFailure);
        }
        catch (TimeoutException ex)
        {
            var step = stepProvider?.Invoke();
            throw new TimeoutException(step == null ? ex.Message : $"The operation timed out at step '{step}'.", ex);
        }
    }

    public LinuxFile CreateMemfdLikeFile(string name, string content)
    {
        Scheduler.AssertSchedulerThread();

        var inode = Runtime.Syscalls.MemfdSuperBlock.AllocInode();
        inode.Type = InodeType.File;
        inode.Mode = 0x180;
        var dentry = new Dentry($"memfd:{name}", inode, Runtime.Syscalls.MemfdSuperBlock.Root,
            Runtime.Syscalls.MemfdSuperBlock);
        var file = new LinuxFile(dentry, FileFlags.O_RDWR, Runtime.Syscalls.AnonMount);

        var payload = Encoding.UTF8.GetBytes(content);
        var rc = inode.WriteFromHost(null, file, payload, 0);
        Assert.Equal(payload.Length, rc);
        return file;
    }

    public async Task<WaylandSocketClient> CreateUnixClientAsync(string path)
    {
        Scheduler.AssertSchedulerThread();
        var handle = CreateClientTaskCore("wayland-client", true);
        var clientSocket = new UnixSocketInode(
            0,
            handle.Syscalls.MemfdSuperBlock,
            SocketType.Stream,
            handle.Task.CommonKernel);
        var clientFile = new LinuxFile(
            new Dentry($"socket:[{clientSocket.Ino}]", clientSocket, null, handle.Syscalls.MemfdSuperBlock),
            FileFlags.O_RDWR,
            handle.Syscalls.AnonMount);
        var endpoint = CreateUnixSockaddr(path);
        var connectRc = await clientSocket.ConnectAsync(clientFile, handle.Task, endpoint);
        Assert.Equal(0, connectRc);
        return new WaylandSocketClient(clientSocket, clientFile, handle.Task);
    }

    private ClientTaskHandle CreateClientTaskCore(string name, bool track)
    {
        var pid = Scheduler.AllocateTaskId();
        var mem = new VMAManager();
        var engine = new Engine();
        var syscalls = Runtime.Syscalls.Clone(mem, false, true);
        syscalls.CurrentSyscallEngine = engine;
        syscalls.RegisterEngine(engine);
        SetCurrentSyscallManager(engine, syscalls);

        var process = new Process(pid, mem, syscalls)
        {
            PGID = pid,
            SID = pid,
            Name = name
        };

        Scheduler.RegisterProcess(process);
        var task = new FiberTask(pid, process, engine, Scheduler)
        {
            Status = FiberTaskStatus.Waiting
        };
        engine.Owner = task;

        var handle = new ClientTaskHandle(process, task, engine, syscalls);
        if (track)
            _clients.Add(handle);
        return handle;
    }

    private void ResetSchedulerThreadBinding()
    {
        OwnerThreadIdField.SetValue(Scheduler, 0);
    }

    private bool WaitForSchedulerReady(TimeSpan timeout)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Scheduler.ScheduleFromAnyThread(() => ready.TrySetResult());
        return ready.Task.Wait(timeout);
    }

    private static void StartScheduledAction<T>(Func<ValueTask<T>> action, TaskCompletionSource<T> tcs)
    {
        ValueTask<T> pending;
        try
        {
            pending = action();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            return;
        }

        if (pending.IsCompletedSuccessfully)
        {
            tcs.TrySetResult(pending.Result);
            return;
        }

        _ = CompleteScheduledActionAsync(pending, tcs);
    }

    private static async Task CompleteScheduledActionAsync<T>(ValueTask<T> pending, TaskCompletionSource<T> tcs)
    {
        try
        {
            tcs.TrySetResult(await pending);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static object CreateUnixSockaddr(string path)
    {
        var endpointType = typeof(SyscallManager).Assembly.GetType("Fiberish.Syscalls.UnixSockaddrInfo")
                           ?? throw new InvalidOperationException("UnixSockaddrInfo type not found.");
        var endpoint = Activator.CreateInstance(endpointType)
                       ?? throw new InvalidOperationException("Failed to create UnixSockaddrInfo.");
        endpointType.GetProperty("IsAbstract", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(endpoint, false);
        endpointType.GetProperty("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(endpoint, path);
        endpointType.GetProperty("SunPathRaw", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(endpoint, Encoding.UTF8.GetBytes($"{path}\0"));
        return endpoint;
    }

    private static void SetCurrentSyscallManager(Engine engine, SyscallManager syscalls)
    {
        typeof(Engine).GetProperty("CurrentSyscallManager",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(engine, syscalls);
    }
}

internal sealed record ClientTaskHandle(Process Process, FiberTask Task, Engine Engine, SyscallManager Syscalls);
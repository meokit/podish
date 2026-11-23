using Fiberish.VFS;
using Podish.Cli.Display;
using Podish.Cli.Wayland;
using Podish.Display;
using Xunit;
using static Podish.Wayland.Tests.WaylandTestHelpers;

namespace Podish.Wayland.Tests;

public sealed class VirtualDrmScanoutTests
{
    [Fact]
    public void FractionalOutputKeepsSharedEdgesAndPreviewAtNativePixels()
    {
        var logical = new DisplaySize(1000, 720);
        var scanout = new DisplaySize(1500, 1080);
        Assert.Equal(new DisplaySize(750, 540),
            WaylandSdlDisplayHost.PixelMatchedWindowSize(logical, new(2000, 1440), scanout));

        var left = AngleDisplayRenderer.GetPixelBounds(new(-1, 3, 2, 2), logical, scanout);
        var right = AngleDisplayRenderer.GetPixelBounds(new(1, 3, 2, 2), logical, scanout);
        Assert.Equal(left.X + left.Width, right.X);
        Assert.Equal(3, left.Width);
        Assert.Equal(3, right.Width);
        Assert.Equal(3, right.Height);
    }

    [Fact]
    public async Task PresentationOnlyCompletesCallbacksIncludedInTheCurrentFlip()
    {
        var commands = new List<WaylandDisplayCommand>();
        using var presenter = new WaylandSdlFramePresenter(WaylandDesktopOptions.Default, commands.Add);
        var server = new WaylandServer(presenter);
        var events = new List<WaylandOutgoingMessage>();
        WaylandClient client = server.CreateClient(message =>
        {
            events.Add(Clone(message));
            return ValueTask.FromResult(message.Buffer.Length);
        });
        await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
        var compositor = ParseGlobals(events, 2).Single(global => global.Interface == WlCompositorProtocol.InterfaceName);
        await SendRequestAsync(client, 2, 0, writer =>
        {
            writer.WriteUInt(compositor.Name);
            writer.WriteString(compositor.Interface);
            writer.WriteUInt(1);
            writer.WriteNewId(3);
        });
        await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(4));
        events.Clear();
        for (uint callbackId = 5; callbackId <= 6; callbackId++)
        {
            await SendRequestAsync(client, 4, 3, writer => writer.WriteNewId(callbackId));
            await SendRequestAsync(client, 4, 6, static _ => { });
        }
        ulong[] sequences = commands.Where(command => command.Kind == WaylandDisplayCommandKind.ScheduleFrame)
            .Select(command => command.FrameSequence).ToArray();
        Assert.Equal(new ulong[] { 1, 2 }, sequences);

        await server.HandlePresentationTickAsync(100, frameSequence: 0);
        Assert.Empty(events);
        await server.HandlePresentationTickAsync(116, sequences[0]);
        Assert.Equal(new uint[] { 5, 1 }, events.Select(message => DecodeHeader(message).ObjectId));
        events.Clear();
        await server.HandlePresentationTickAsync(133, sequences[1]);
        Assert.Equal(new uint[] { 6, 1 }, events.Select(message => DecodeHeader(message).ObjectId));
        await server.ShutdownAsync();
    }

    [Fact]
    public async Task VirtualCardPresentsCompletedFlipsWithoutOverwritingTheFrontBuffer()
    {
        using var env = new TestEnv();
        var scanout = new VirtualDrmScanout(80, 48);
        env.InvokeOnScheduler(() => new DrmCardInode(
            new Tmpfs().ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null),
            env.Scheduler, gpuBuffers: false, compositorOutput: scanout));
        try
        {
            Assert.True(scanout.TryQueueFrame(pixels =>
            {
                pixels.Span[0] = 42;
                Assert.False(scanout.TryTakeFrame(out _, out _));
            }));
            Assert.False(scanout.TryQueueFrame(_ => throw new InvalidOperationException("No back buffer is available")));

            var (first, firstTimestamp) = await TakeFrameAsync(scanout);
            Assert.Equal(80 * 48 * 4, first.Length);
            Assert.Equal(42, first.Span[0]);

            Assert.True(scanout.TryQueueFrame(pixels =>
            {
                pixels.Span[0] = 99;
                Assert.Equal(42, first.Span[0]);
            }));

            var (second, secondTimestamp) = await TakeFrameAsync(scanout);
            Assert.Equal(99, second.Span[0]);
            Assert.InRange(unchecked(secondTimestamp - firstTimestamp), 1u, 2500u);
        }
        finally
        {
            env.InvokeOnScheduler(scanout.Detach);
        }

        Assert.False(scanout.TryQueueFrame(_ => throw new InvalidOperationException("The card is detached")));
        Assert.False(scanout.TryTakeFrame(out _, out _));
    }

    [Fact]
    public async Task VirtualKmsPresentsOnlyTheGpuBufferThatCompletedAFlip()
    {
        using var env = new TestEnv();
        using var ready = new SemaphoreSlim(0);
        var scanout = new VirtualDrmScanout(80, 48);
        var first = new TestGpuBuffer();
        var second = new TestGpuBuffer();
        scanout.SetGpuBuffers(first, second);
        scanout.FrameReady += () => ready.Release();
        env.InvokeOnScheduler(() => new DrmCardInode(
            new Tmpfs().ReadSuper(new FileSystemType { Name = "tmpfs" }, 0, "", null),
            env.Scheduler, gpuBuffers: true, compositorOutput: scanout));
        try
        {
            Assert.True(scanout.TryQueueGpuFrame(buffer =>
            {
                Assert.Same(second, buffer);
                Assert.False(scanout.TryTakeGpuFrame(out _, out _));
            }));
            Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(scanout.TryTakeGpuFrame(out var completed, out _));
            Assert.Same(second, completed);
            Assert.True(second.InUse);
            Assert.Equal(0, second.PresentCount);
            Assert.True(completed!.Present());

            Assert.True(scanout.TryQueueGpuFrame(buffer => Assert.Same(first, buffer)));
            Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(scanout.TryTakeGpuFrame(out completed, out _));
            Assert.Same(first, completed);
            Assert.False(second.InUse);
        }
        finally
        {
            env.InvokeOnScheduler(scanout.Detach);
            first.Release();
            second.Release();
        }

        Assert.True(first.Destroyed);
        Assert.True(second.Destroyed);
    }

    private sealed class TestGpuBuffer() : DrmScanoutBuffer(80, 48, 80 * 4)
    {
        public override bool Ready => true;
        public int PresentCount { get; private set; }
        public bool Destroyed { get; private set; }
        public override bool Present()
        {
            PresentCount++;
            return true;
        }

        protected override void Destroy() => Destroyed = true;
    }

    private static async Task<(ReadOnlyMemory<byte> Pixels, uint Timestamp)> TakeFrameAsync(VirtualDrmScanout scanout)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (scanout.TryTakeFrame(out var pixels, out uint timestamp))
                return (pixels, timestamp);
            await Task.Delay(1, timeout.Token);
        }
    }
}

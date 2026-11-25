using Fiberish.VFS;
using Xunit;
using static Podish.Wayland.Tests.WaylandTestHelpers;

namespace Podish.Wayland.Tests;

public sealed class WaylandExplicitSyncTests
{
    [Theory]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, true, true)]
    public async Task AdvertisesOnlyModifiersSupportedByTheBackend(bool legacy, bool venus, bool virgl,
        bool explicitSync, bool linear)
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var server = new WaylandServer(new GpuPresenter(), enableGpuBuffers: legacy,
                enableExplicitSync: explicitSync, enableVenusPrime: venus, enableVirglPrime: virgl);
            var sent = new List<WaylandOutgoingMessage>();
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var dmabuf = ParseGlobals(sent, 2).SingleOrDefault(entry =>
                entry.Interface == ZwpLinuxDmabufV1Protocol.InterfaceName);
            if (!legacy && !linear)
            {
                Assert.Null(dmabuf);
                return;
            }

            Assert.NotNull(dmabuf);
            sent.Clear();
            await SendRequestAsync(client, 2, 0, writer =>
            {
                writer.WriteUInt(dmabuf.Name);
                writer.WriteString(dmabuf.Interface);
                writer.WriteUInt(dmabuf.Version);
                writer.WriteNewId(3);
            });
            var modifiers = sent.Where(message => DecodeHeader(message) is { ObjectId: 3, Opcode: 1 })
                .Select(ReadModifier).ToArray();
            Assert.Equal(legacy ? 2 : 0, modifiers.Count(modifier => modifier == 0x00ff_ffff_ffff_ffff));
            Assert.Equal(linear ? 2 : 0, modifiers.Count(modifier => modifier == 0));
            Assert.Equal((legacy ? 2 : 0) + (linear ? 2 : 0), modifiers.Length);
        });
    }

    private static ulong ReadModifier(WaylandOutgoingMessage message)
    {
        var reader = new WaylandWireReader(message.Buffer[WaylandMessageHeader.SizeInBytes..]);
        reader.ReadUInt(); // DRM FourCC.
        return ((ulong)reader.ReadUInt() << 32) | reader.ReadUInt();
    }

    private sealed class GpuPresenter : IWaylandFramePresenter, IWaylandGpuFramePresenter
    {
        public bool CanImportGpuBuffer(DrmScanoutBuffer buffer) => true;
        public ValueTask PresentGpuSurfaceAsync(ulong sceneSurfaceId, WaylandGpuFrame frame) => ValueTask.CompletedTask;
        public ValueTask PresentSurfaceAsync(ulong sceneSurfaceId, WaylandShmFrame? frame,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseWaitsForDisplayConsumptionEvenIfBufferWasDestroyed(bool destroyBuffer)
    {
        using var env = new TestEnv();
        await env.InvokeOnSchedulerAsync(async () =>
        {
            var sent = new List<WaylandOutgoingMessage>();
            var presenter = new RecordingFramePresenter();
            var server = new WaylandServer(presenter, enableExplicitSync: true);
            Assert.DoesNotContain(new WaylandServer(presenter).Globals.Globals,
                global => global.Interface == ZwpLinuxExplicitSynchronizationV1Protocol.InterfaceName);
            var client = server.CreateClient(message =>
            {
                sent.Add(Clone(message));
                return new ValueTask<int>(message.Buffer.Length);
            });

            await SendRequestAsync(client, 1, 1, writer => writer.WriteNewId(2));
            var globals = ParseGlobals(sent, 2);
            await BindAsync(WlCompositorProtocol.InterfaceName, 3);
            await BindAsync(WlShmProtocol.InterfaceName, 4);
            await BindAsync(ZwpLinuxExplicitSynchronizationV1Protocol.InterfaceName, 5);

            await SendRequestAsync(client, 3, 0, writer => writer.WriteNewId(6));
            await SendRequestAsync(client, 5, 1, writer =>
            {
                writer.WriteNewId(7);
                writer.WriteObjectId(6);
            });
            var file = env.CreateMemfdLikeFile("explicit-sync", new string('a', 16));
            await SendRequestAsync(client, 4, 0, writer =>
            {
                writer.WriteNewId(8);
                writer.WriteFd(file);
                writer.WriteInt(16);
            });
            await SendRequestAsync(client, 8, 0, writer =>
            {
                writer.WriteNewId(9);
                writer.WriteInt(0);
                writer.WriteInt(2);
                writer.WriteInt(2);
                writer.WriteInt(8);
                writer.WriteUInt((uint)WlShmFormat.Argb8888);
            });
            await SendRequestAsync(client, 7, 2, writer => writer.WriteNewId(10));
            await SendRequestAsync(client, 6, 1, writer =>
            {
                writer.WriteObjectId(9);
                writer.WriteInt(0);
                writer.WriteInt(0);
            });
            await SendRequestAsync(client, 6, 6, static _ => { });
            var frame = Assert.IsType<WaylandShmFrame>(presenter.LastFrame);
            Assert.DoesNotContain(sent, item => DecodeHeader(item).ObjectId == 10);

            if (destroyBuffer)
                await SendRequestAsync(client, 9, 0, static _ => { });
            sent.Clear();
            await server.HandleBufferConsumedAsync(frame.LeaseToken);
            Assert.Contains(sent, item => DecodeHeader(item) is { ObjectId: 10, Opcode: 1 });
            Assert.Equal(!destroyBuffer, sent.Any(item => DecodeHeader(item) is { ObjectId: 9, Opcode: 0 }));

            sent.Clear();
            await server.HandleBufferConsumedAsync(frame.LeaseToken);
            Assert.Empty(sent);

            var invalidFd = env.CreateMemfdLikeFile("not-a-fence", "ordinary shared memory");
            var invalidFence = await Assert.ThrowsAsync<WaylandProtocolException>(() =>
                SendRequestAsync(client, 7, 1, writer => writer.WriteFd(invalidFd)));
            Assert.Equal((uint)ZwpLinuxSurfaceSynchronizationV1Error.InvalidFence, invalidFence.ErrorCode);
            await server.DisconnectClientAsync(client);

            Task BindAsync(string name, uint id)
            {
                var global = globals.Single(entry => entry.Interface == name);
                return SendRequestAsync(client, 2, 0, writer =>
                {
                    writer.WriteUInt(global.Name);
                    writer.WriteString(name);
                    writer.WriteUInt(global.Version);
                    writer.WriteNewId(id);
                });
            }
        });
    }
}

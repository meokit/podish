using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.VFS;
using Xunit;

namespace Fiberish.Tests.Syscalls;

public partial class FsFdReviewRegressionTests
{
    private const uint ColdAddress = 0x40000;
    private const uint SecondColdAddress = 0x50000;
    private const uint GuestPageSize = 4096;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipeIovecFaultsPreserveAnAtomicWriteAndPacketBoundary(bool packet)
    {
        var source = new byte[2 * GuestPageSize];
        "COLD"u8.CopyTo(source.AsSpan((int)GuestPageSize - 2));
        var layer = new LayerContent(("/source", source), ("/destination", new byte[GuestPageSize]));
        using var env = new TestEnv();
        MapLayerFile(env, layer, "/source", ColdAddress, 2 * GuestPageSize, Protection.Read);
        MapLayerFile(env, layer, "/destination", SecondColdAddress, GuestPageSize, Protection.Read | Protection.Write);
        var (pipe, reader, writer) = env.AddPipe(new PipeInode(env.Scheduler, packetMode: packet));
        var firstByte = ColdAddress + GuestPageSize - 2;
        env.WriteIovecs((firstByte, 2), (firstByte + 2, 2));

        Assert.Empty(layer.Reads);
        Assert.Equal(4, await env.Call("SysWriteV", (uint)writer, IovPtr, 2));
        Assert.Contains(("/source", 0), layer.Reads);
        Assert.Contains(("/source", (long)GuestPageSize), layer.Reads);
        Assert.Equal(1, pipe.WriteFromHost(env.Task, env.Sm.GetFD(writer)!, "!"u8.ToArray(), 0));

        var count = packet ? 2u : 4u;
        Assert.Equal((int)count, await env.Call("SysRead", (uint)reader, SecondColdAddress, count));
        Assert.Contains(("/destination", 0), layer.Reads);
        var transferred = new byte[(int)count];
        Assert.True(env.Engine.CopyFromUser(SecondColdAddress, transferred));
        Assert.Equal("COLD"u8[..(int)count].ToArray(), transferred);
        var tail = new byte[8];
        Assert.Equal(1, pipe.ReadToHost(env.Task, env.Sm.GetFD(reader)!, tail, 0));
        Assert.Equal((byte)'!', tail[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipeReadChecksColdDestinationBeforeConsuming(bool revokeWriteAccess)
    {
        using var env = new TestEnv();
        var provider = new DeferredLayerContent();
        var index = new LayerIndex();
        index.AddEntry(new LayerIndexEntry("/destination", InodeType.File, 0x1B6, Size: 4));
        var fs = new LayerFileSystem(memoryContext: env.Sm.MemoryContext);
        var sb = fs.ReadSuper(new FileSystemType { Name = "layerfs" }, 0, "deferred-pipe-test",
            new LayerMountOptions { Index = index, ContentProvider = provider });
        var file = new LinuxFile(sb.Root.Inode!.Lookup("destination")!, FileFlags.O_RDONLY, env.Sm.RootMount);
        env.Mem.Mmap(ColdAddress, GuestPageSize, Protection.Read | Protection.Write, MapFlags.Fixed | MapFlags.Private,
            file, 0, "[deferred-pipe-test]", env.Engine);
        var (pipe, reader, writer) = env.AddPipe();
        Assert.Equal(4, pipe.WriteFromHost(env.Task, env.Sm.GetFD(writer)!, "DATA"u8.ToArray(), 0));

        var pending = env.Call("SysRead", (uint)reader, ColdAddress, 4);
        Assert.False(pending.IsCompleted);
        var queued = new byte[4];
        Assert.Equal(4, pipe.Peek(queued));
        Assert.Equal("DATA"u8.ToArray(), queued);

        if (revokeWriteAccess)
            Assert.Equal(0, await env.Call("SysMprotect", ColdAddress, GuestPageSize, (uint)Protection.Read));
        provider.Complete(new byte[4]);
        var result = await pending;
        Assert.Equal(0ul, env.Task.PendingSignals);

        if (revokeWriteAccess)
        {
            Assert.Equal(-(int)Errno.EFAULT, result);
            Assert.Equal(4, pipe.Peek(queued));
            Assert.Equal("DATA"u8.ToArray(), queued);
        }
        else
        {
            var readerFile = env.Sm.GetFD(reader)!;
            Assert.Equal(4, result);
            Assert.Equal(0, pipe.Poll(readerFile, 1) & 1);
            Assert.True(env.Engine.CopyFromUser(ColdAddress, queued));
            Assert.Equal("DATA"u8.ToArray(), queued);
        }
    }

    [Fact]
    public async Task SocketSendAndReceiveResolveColdMappedPayloads()
    {
        var layer = new LayerContent(("/source", "SOCKET"u8.ToArray()), ("/destination", new byte[6]));
        using var env = new TestEnv();
        MapLayerFile(env, layer, "/source", ColdAddress, GuestPageSize, Protection.Read);
        MapLayerFile(env, layer, "/destination", SecondColdAddress, GuestPageSize, Protection.Read | Protection.Write);
        Assert.Equal(0, await env.Call("SysSocketPair", LinuxConstants.AF_UNIX,
            LinuxConstants.SOCK_DGRAM, 0, InfoPtr));
        var descriptors = new byte[8];
        Assert.True(env.Engine.CopyFromUser(InfoPtr, descriptors));
        var send = BinaryPrimitives.ReadUInt32LittleEndian(descriptors);
        var receive = BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan(4));

        Assert.Empty(layer.Reads);
        Assert.Equal(6, await env.Call("SysSendTo", send, ColdAddress, 6));
        Assert.Contains(("/source", 0), layer.Reads);
        Assert.Equal(6, await env.Call("SysRecvFrom", receive, SecondColdAddress, 6, LinuxConstants.MSG_DONTWAIT));
        Assert.Contains(("/destination", 0), layer.Reads);
        var payload = new byte[6];
        Assert.True(env.Engine.CopyFromUser(SecondColdAddress, payload));
        Assert.Equal("SOCKET"u8.ToArray(), payload);
    }

    [Theory]
    [InlineData("SysSendfile", false)]
    [InlineData("SysSendfile64", false)]
    [InlineData("SysSplice", false)]
    [InlineData("SysSplice", true)]
    [InlineData("SysCopyFileRange", false)]
    public async Task FileTransferCopiesAndUpdatesColdMappedOffsets(string call, bool fromPipe)
    {
        var width = call == "SysSendfile" ? 4 : 8;
        var offset = new byte[width];
        offset[0] = 1;
        var layer = new LayerContent(("/offset", offset), ("/output-offset", new byte[width]));
        using var env = new TestEnv();
        MapLayerFile(env, layer, "/offset", ColdAddress, GuestPageSize, Protection.Read | Protection.Write);
        var input = (uint)env.AddFile("input", "ABCDE");
        var output = (uint)env.AddFile("output", fromPipe ? "_" : "");
        var (pipe, reader, writer) = env.AddPipe();

        int transferred;
        if (call == "SysCopyFileRange")
        {
            MapLayerFile(env, layer, "/output-offset", SecondColdAddress, GuestPageSize, Protection.Read | Protection.Write);
            transferred = await env.Call(call, input, ColdAddress, output, SecondColdAddress, 3);
        }
        else if (call == "SysSplice" && fromPipe)
        {
            Assert.Equal(3, pipe.WriteFromHost(env.Task, env.Sm.GetFD(writer)!, "BCD"u8.ToArray(), 0));
            transferred = await env.Call(call, (uint)reader, 0, output, ColdAddress, 3, 2);
        }
        else if (call == "SysSplice")
        {
            transferred = await env.Call(call, input, ColdAddress, (uint)writer, 0, 3, 2);
        }
        else
        {
            transferred = await env.Call(call, output, input, ColdAddress, 3);
        }

        Assert.Equal(3, transferred);
        Assert.Contains(("/offset", 0), layer.Reads);
        Assert.Equal(0, env.Sm.GetFD((int)input)!.Position);
        var result = new byte[width];
        Assert.True(env.Engine.CopyFromUser(ColdAddress, result));
        Assert.Equal(4, result[0]);
        Assert.All(result.Skip(1), value => Assert.Equal(0, value));
        if (call == "SysSplice" && !fromPipe)
        {
            var received = new byte[3];
            Assert.Equal(3, pipe.ReadToHost(env.Task, env.Sm.GetFD(reader)!, received, 0));
            Assert.Equal("BCD"u8.ToArray(), received);
        }
        else
        {
            Assert.Equal(fromPipe ? "_BCD" : "BCD", env.FileContents((int)output));
        }

        if (call == "SysCopyFileRange")
        {
            Assert.Contains(("/output-offset", 0), layer.Reads);
            Assert.True(env.Engine.CopyFromUser(SecondColdAddress, result));
            Assert.Equal(3, BinaryPrimitives.ReadInt64LittleEndian(result));
            Assert.Equal(0, env.Sm.GetFD((int)output)!.Position);
        }
    }

    [Fact]
    public async Task ExecveReadsColdPathAndArgumentVectorsBeforeExecutableLookup()
    {
        var argv = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(argv, SecondColdAddress + 8);
        "cold argument\0"u8.CopyTo(argv.AsSpan(8));
        var envp = new byte[24];
        const uint environmentAddress = SecondColdAddress + GuestPageSize;
        BinaryPrimitives.WriteUInt32LittleEndian(envp, environmentAddress + 8);
        "COLD=1\0"u8.CopyTo(envp.AsSpan(8));
        var layer = new LayerContent(("/path", "/missing-program\0"u8.ToArray()), ("/argv", argv), ("/envp", envp));
        using var env = new TestEnv();
        MapLayerFile(env, layer, "/path", ColdAddress, GuestPageSize, Protection.Read);
        MapLayerFile(env, layer, "/argv", SecondColdAddress, GuestPageSize, Protection.Read);
        MapLayerFile(env, layer, "/envp", environmentAddress, GuestPageSize, Protection.Read);
        Assert.Empty(layer.Reads);

        Assert.Equal(-(int)Errno.ENOENT, await env.Call("SysExecve", ColdAddress, SecondColdAddress, environmentAddress));
        Assert.Equal(new[] { ("/path", 0L), ("/argv", 0L), ("/envp", 0L) }, layer.Reads.Distinct());
        Assert.Equal(0ul, env.Task.PendingSignals);
    }

    [Fact]
    public async Task SharedWritableOverlayFaultCopiesUpAndLeavesLowerBytesIntact()
    {
        var layer = new LayerContent(("/data", "lower"u8.ToArray()));
        using var env = new TestEnv(overlay: true, layerContent: layer);
        env.WriteString(PathPtr, "/data");
        var fd = await env.Call("SysOpen", PathPtr, (uint)FileFlags.O_RDWR);
        Assert.True(fd >= 0);
        var file = env.Sm.GetFD(fd)!;
        var inode = Assert.IsType<OverlayInode>(file.OpenedInode);
        Assert.Null(inode.UpperInode);

        Assert.Equal(ColdAddress, (uint)await env.Call("SysMmap2", ColdAddress, GuestPageSize,
            (uint)(Protection.Read | Protection.Write), (uint)(MapFlags.Shared | MapFlags.Fixed), (uint)fd));
        Assert.Empty(layer.Reads);
        Assert.True(env.Engine.CopyToUser(ColdAddress, "U"u8));
        Assert.Contains(("/data", 0), layer.Reads);
        Assert.NotNull(inode.UpperInode);
        Assert.Equal("Uower", env.FileContents(fd));
        var lower = new byte[5];
        Assert.Equal(5, inode.LowerInode!.ReadToHost(env.Task, file, lower, 0));
        Assert.Equal("lower"u8.ToArray(), lower);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpandingALockedFileMappingFaultsInTheColdTail(bool move)
    {
        var data = new byte[2 * GuestPageSize];
        data[0] = 0x11;
        data[GuestPageSize] = 0x22;
        var layer = new LayerContent(("/mapped", data));
        using var env = new TestEnv();
        var file = OpenLayerFile(env, layer, "/mapped");
        var fd = env.Sm.AllocFD(file);

        Assert.Equal(ColdAddress, (uint)await env.Call("SysMmap2", ColdAddress, GuestPageSize, (uint)Protection.Read,
            (uint)(MapFlags.Private | MapFlags.Fixed | MapFlags.Locked), (uint)fd));
        Assert.Equal(new[] { ("/mapped", 0L) }, layer.Reads.Distinct());
        var expectedAddress = move ? SecondColdAddress : ColdAddress;
        Assert.Equal(expectedAddress, (uint)await env.Call("SysMremap", ColdAddress, GuestPageSize,
            2 * GuestPageSize, move ? 3u : 1u, move ? SecondColdAddress : 0));

        Assert.Contains(("/mapped", (long)GuestPageSize), layer.Reads);
        Assert.True((env.Mem.FindVmArea(expectedAddress + GuestPageSize)!.Flags & MapFlags.Locked) != 0);
        var tail = new byte[1];
        Assert.True(env.Engine.CopyFromUser(expectedAddress + GuestPageSize, tail));
        Assert.Equal(0x22, tail[0]);
    }

    [Fact]
    public async Task RemapFilePagesKeepsALockedFileVmaResidentAtTheNewOffset()
    {
        var data = new byte[2 * GuestPageSize];
        data[GuestPageSize] = 0x5a;
        var layer = new LayerContent(("/mapped", data));
        using var env = new TestEnv();
        var fd = env.Sm.AllocFD(OpenLayerFile(env, layer, "/mapped"));
        Assert.Equal(ColdAddress, (uint)await env.Call("SysMmap2", ColdAddress, GuestPageSize, (uint)Protection.Read,
            (uint)(MapFlags.Shared | MapFlags.Fixed | MapFlags.Locked), (uint)fd));
        Assert.Equal(new[] { ("/mapped", 0L) }, layer.Reads.Distinct());

        Assert.Equal(0, await env.Call("SysRemapFilePages", ColdAddress, GuestPageSize, 0, 1));
        Assert.Contains(("/mapped", (long)GuestPageSize), layer.Reads);
        Assert.True(env.Mem.PageMapping.TryGet(ColdAddress, out _));
        var mappedByte = new byte[1];
        Assert.True(env.Engine.CopyFromUser(ColdAddress, mappedByte));
        Assert.Equal(0x5a, mappedByte[0]);
    }

    private static void MapLayerFile(TestEnv env, LayerContent layer, string path, uint address, uint length,
        Protection protection)
    {
        env.Mem.Mmap(address, length, protection, MapFlags.Fixed | MapFlags.Private, OpenLayerFile(env, layer, path),
            0, "[cold-layer-test]", env.Engine);
        Assert.False(env.Mem.PageMapping.TryGet(address, out _));
    }

    private static LinuxFile OpenLayerFile(TestEnv env, LayerContent layer, string path)
    {
        var sb = layer.Mount(env.Sm.MemoryContext);
        var dentry = sb.Root.Inode!.Lookup(path[1..])!;
        return new LinuxFile(dentry, FileFlags.O_RDONLY, env.Sm.RootMount);
    }

    private sealed class LayerContent(params (string Path, byte[] Data)[] files) : IAsyncLayerContentProvider
    {
        public List<(string Path, long Offset)> Reads { get; } = [];

        public SuperBlock Mount(MemoryRuntimeContext context)
        {
            var index = new LayerIndex();
            foreach (var (path, data) in files)
                index.AddEntry(new LayerIndexEntry(path, InodeType.File, 0x1B6, Size: (ulong)data.Length));
            return new LayerFileSystem(memoryContext: context).ReadSuper(new FileSystemType { Name = "layerfs" },
                0, "cold-layer-test", new LayerMountOptions
                {
                    Index = index,
                    ContentProvider = this,
                    MinimumReadAheadBytes = (int)GuestPageSize
                });
        }

        public bool TryRead(LayerIndexEntry entry, long offset, Span<byte> buffer, out int bytesRead)
        {
            Reads.Add((entry.Path, offset));
            var data = files.Single(file => file.Path == entry.Path).Data;
            bytesRead = Math.Min(buffer.Length, Math.Max(0, data.Length - (int)offset));
            if (bytesRead > 0) data.AsSpan((int)offset, bytesRead).CopyTo(buffer);
            return true;
        }

        public ValueTask<int> ReadAsync(LayerIndexEntry entry, long offset, Memory<byte> buffer, FiberTask task,
            bool noWait)
        {
            TryRead(entry, offset, buffer.Span, out var count);
            return ValueTask.FromResult(count);
        }
    }
}

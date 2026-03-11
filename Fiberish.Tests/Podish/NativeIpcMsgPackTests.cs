using System.Buffers;
using System.Runtime.InteropServices;
using MessagePack;
using Microsoft.Extensions.Logging;
using Podish.Core;
using Podish.Core.Native;
using Xunit;
using Xunit.Sdk;

namespace Fiberish.Tests.Podish;

public class NativeIpcMsgPackTests
{
    [Fact]
    public void PodCtxCallMsgPack_PollEvent_WhenIdle_ReturnsNoneEvent()
    {
        using var harness = NativeIpcHarness.Create();

        var args = EncodePollArgs(0);
        var frame = InvokePollEvent(harness.CtxHandle, args);
        var (eventType, _, _) = DecodeEvent(frame);
        Assert.Equal(PodishNativeApi.PodIpcEventNone, eventType);
    }

    [Fact]
    public void PodCtxCallMsgPack_PollEvent_ReturnsLogEvent()
    {
        using var harness = NativeIpcHarness.Create("info");
        var marker = "ipc-log-" + Guid.NewGuid().ToString("N");

        harness.Context.Context.LoggerFactory.CreateLogger("native-ipc-test").LogInformation("{Marker}", marker);

        for (var i = 0; i < 20; i++)
        {
            var args = EncodePollArgs(200);
            var frame = InvokePollEvent(harness.CtxHandle, args);
            var (eventType, _, message) = DecodeEvent(frame);
            if (eventType == PodishNativeApi.PodIpcEventLogLine && message.Contains(marker, StringComparison.Ordinal))
                return;
        }

        throw new XunitException("Did not receive expected log event");
    }

    [Fact]
    public void PodCtxCallMsgPack_PollEvent_ReturnsContainerStateChangedEvent()
    {
        using var harness = NativeIpcHarness.Create();

        harness.Context.EmitContainerStateChanged();

        for (var i = 0; i < 10; i++)
        {
            var args = EncodePollArgs(100);
            var frame = InvokePollEvent(harness.CtxHandle, args);
            var (eventType, _, _) = DecodeEvent(frame);
            if (eventType == PodishNativeApi.PodIpcEventContainerStateChanged)
                return;
        }

        throw new XunitException("Did not receive container state changed event");
    }

    [Fact]
    public void PodCtxCallMsgPack_UnsupportedOp_ReturnsEinval()
    {
        using var harness = NativeIpcHarness.Create();
        var response = new byte[32];
        var rc = PodishNativeApi.PodCtxCallMsgPackManaged(harness.CtxHandle, 9999, ReadOnlySpan<byte>.Empty, response,
            out _);
        Assert.Equal(PodishNativeApi.PodEinval, rc);
    }

    [Fact]
    public void PodCtxCallMsgPack_InvalidArgs_ReturnsEinval()
    {
        using var harness = NativeIpcHarness.Create();
        var response = new byte[32];
        var invalidArgs = new byte[] { 0xC1 }; // Reserved token in MessagePack.
        var rc = PodishNativeApi.PodCtxCallMsgPackManaged(harness.CtxHandle, PodishNativeApi.PodIpcOpPollEvent,
            invalidArgs, response, out _);
        Assert.Equal(PodishNativeApi.PodEinval, rc);
    }

    private static byte[] InvokePollEvent(IntPtr ctxHandle, byte[] args)
    {
        var response = new byte[4096];
        var rc = PodishNativeApi.PodCtxCallMsgPackManaged(ctxHandle, PodishNativeApi.PodIpcOpPollEvent, args, response,
            out var outLen);
        Assert.Equal(PodishNativeApi.PodOk, rc);

        return response.AsSpan(0, outLen).ToArray();
    }

    private static byte[] EncodePollArgs(int timeoutMs)
    {
        var buffer = new ArrayBufferWriter<byte>(16);
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(1);
        writer.Write(timeoutMs);
        writer.Flush();
        return buffer.WrittenMemory.ToArray();
    }

    private static (int EventType, int Level, string Message) DecodeEvent(byte[] frame)
    {
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(frame));
        var count = reader.ReadArrayHeader();
        var eventType = reader.ReadInt32();
        if (eventType == PodishNativeApi.PodIpcEventLogLine)
        {
            Assert.True(count >= 3);
            var level = reader.ReadInt32();
            var message = reader.ReadString() ?? string.Empty;
            return (eventType, level, message);
        }

        return (eventType, 0, string.Empty);
    }

    private sealed class NativeIpcHarness : IDisposable
    {
        private readonly GCHandle _ctxHandle;
        private readonly string _root;

        private NativeIpcHarness(string root, NativeContext context, GCHandle ctxHandle)
        {
            _root = root;
            Context = context;
            _ctxHandle = ctxHandle;
        }

        public NativeContext Context { get; }
        public IntPtr CtxHandle => GCHandle.ToIntPtr(_ctxHandle);

        public void Dispose()
        {
            if (_ctxHandle.IsAllocated)
                _ctxHandle.Free();
            Context.Dispose();
            Context.Context.Dispose();
            Directory.Delete(_root, true);
        }

        public static NativeIpcHarness Create(string logLevel = "error")
        {
            var root = Path.Combine(Path.GetTempPath(), "podish-native-ipc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var context = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = root,
                    LogFile = Path.Combine(root, "podish.log"),
                    LogLevel = logLevel
                })
            };
            var handle = GCHandle.Alloc(context, GCHandleType.Normal);
            return new NativeIpcHarness(root, context, handle);
        }
    }
}
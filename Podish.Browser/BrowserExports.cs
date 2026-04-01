using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Microsoft.Extensions.Logging;
using Podish.Core;

namespace Podish.Browser;

[SupportedOSPlatform("browser")]
public static partial class BrowserExports
{
    private const int IrqOutputReady = 1 << 1;
    private const int StackLogPayloadThreshold = 512;
    private const int PooledLogPayloadSize = 4096;
    private const byte ControlStopSession = 1;
    private const byte ControlSessionExited = 2;

    private static readonly Lock Sync = new();
    private static BrowserSessionState? _session;

    static BrowserExports()
    {
        BrowserSchedulerHostBridge.Configure(
            BrowserSabInterop.IrqInputReady,
            BrowserSabInterop.IrqOutputDrained,
            BrowserSabInterop.IrqTimer,
            BrowserSabInterop.IrqSchedulerWake,
            RequestTimer,
            CancelTimer,
            SignalInterrupt,
            BrowserSabInterop.WaitForInterrupt,
            BrowserSabInterop.PollInterrupt,
            DispatchPendingInputEvents,
            DispatchPendingOutputEvents);
    }

    [JSImport("signalInterrupt", "podish-worker.mjs")]
    internal static partial void SignalInterrupt(int bits);

    [JSImport("requestTimer", "podish-worker.mjs")]
    internal static partial void RequestTimer(int delayMs);

    [JSImport("cancelTimer", "podish-worker.mjs")]
    internal static partial void CancelTimer();

    [JSExport]
    public static string GetRuntimeInfo()
    {
        return $"Podish.Core loaded: {typeof(PodishContext).Assembly.GetName().Name}";
    }

    [JSExport]
    public static async Task<string> StartRootfsTarShell(byte[] rootfsTarBytes, int rows = 24, int cols = 80)
    {
        if (rootfsTarBytes.Length == 0)
            return Json(false, error: "rootfs tar is empty");

        BrowserSessionState? previous;
        lock (Sync)
        {
            previous = _session;
            _session = null;
        }

        if (previous != null)
            await DisposeSessionAsync(previous);

        var workDir = Path.Combine(Environment.CurrentDirectory, ".browserwasm");
        Directory.CreateDirectory(workDir);

        var context = new PodishContext(new PodishContextOptions
        {
            WorkDir = workDir,
            LogLevel = "warn"
        });

        context.SetLogObserver(EmitManagedLog);

        try
        {
            await using var stream = new MemoryStream(rootfsTarBytes, false);
            var session = await context.StartRootfsTarAsync(stream, new PodishRunSpec
            {
                Name = "browser-shell",
                Hostname = "browser-shell",
                Rootfs = "uploaded-rootfs.tar",
                Exe = "/bin/sh",
                ExeArgs = [],
                Interactive = true,
                Tty = true,
                TerminalRows = (ushort)rows,
                TerminalCols = (ushort)cols,
                Strace = false,
                NetworkMode = NetworkMode.Host
            }, "uploaded-rootfs.tar", "browserwasm");

            var state = new BrowserSessionState(context, session);
            state.Dispatcher.Register(BrowserSabQueueKind.Input, BrowserSabInterop.EventInputBytes, payload =>
            {
                if (payload.IsEmpty)
                    return;

                session.WriteInput(payload);
            });
            state.Dispatcher.Register(BrowserSabQueueKind.Input, BrowserSabInterop.EventResize, payload =>
            {
                if (!BrowserEventDispatcher.TryParseResize(payload, out var resizeRows, out var resizeCols))
                    return;

                session.Resize(resizeRows, resizeCols);
            });
            state.Dispatcher.Register(BrowserSabQueueKind.Input, BrowserSabInterop.EventControl, payload =>
            {
                if (payload.IsEmpty)
                    return;

                if (payload[0] == ControlStopSession)
                    session.ForceStop();
            });
            session.SetOutputHandler((_, data) =>
            {
                if (!data.IsEmpty)
                    state.EnqueueOutput(data);
            });

            lock (Sync)
            {
                _session = state;
            }

            return Json(true, containerId: session.ContainerId, imageRef: session.ImageRef);
        }
        catch (Exception ex)
        {
            context.Dispose();
            return Json(false, error: ex.ToString());
        }
    }

    internal static int DispatchPendingInputEvents(int maxPackets = 64)
    {
        var session = GetSession();
        if (session == null)
            return 0;

        return session.Dispatcher.DispatchQueue(BrowserSabQueueKind.Input, maxPackets);
    }

    internal static int DispatchPendingOutputEvents(int maxPackets = 64)
    {
        var session = GetSession();
        if (session == null)
            return 0;

        return session.FlushPendingOutput(maxPackets);
    }

    [JSExport]
    public static async Task RunCurrentSession()
    {
        var session = GetSession();
        if (session == null)
            return;

        await session.RunUntilExitAsync();
    }

    internal static async Task<string> StopSession()
    {
        BrowserSessionState? session;
        lock (Sync)
        {
            session = _session;
            _session = null;
        }

        if (session == null)
            return Json(true, message: "no active session");

        await DisposeSessionAsync(session);
        return Json(true);
    }

    private static BrowserSessionState? GetSession()
    {
        lock (Sync)
        {
            return _session;
        }
    }

    private static async Task DisposeSessionAsync(BrowserSessionState session)
    {
        try
        {
            session.Session.ForceStop();
            await session.Session.WaitAsync();
        }
        catch
        {
        }
        finally
        {
            session.Context.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }


    private static string Json(
        bool? ok = null,
        bool? hasSession = null,
        bool? running = null,
        int? exitCode = null,
        string? error = null,
        string? message = null,
        string? containerId = null,
        string? imageRef = null)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;

        AppendBool("ok", ok);
        AppendBool("hasSession", hasSession);
        AppendBool("running", running);
        AppendInt("exitCode", exitCode);
        AppendString("error", error);
        AppendString("message", message);
        AppendString("containerId", containerId);
        AppendString("imageRef", imageRef);

        sb.Append('}');
        return sb.ToString();

        void AppendSeparator()
        {
            if (first)
            {
                first = false;
                return;
            }

            sb.Append(',');
        }

        void AppendBool(string name, bool? value)
        {
            if (!value.HasValue)
                return;

            AppendSeparator();
            sb.Append('"').Append(name).Append("\":").Append(value.Value ? "true" : "false");
        }

        void AppendInt(string name, int? value)
        {
            if (!value.HasValue)
                return;

            AppendSeparator();
            sb.Append('"').Append(name).Append("\":").Append(value.Value);
        }

        void AppendString(string name, string? value)
        {
            if (value == null)
                return;

            AppendSeparator();
            sb.Append('"').Append(name).Append("\":\"").Append(EscapeJson(value)).Append('"');
        }
    }

    private static void EmitManagedLog(LogLevel level, string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        var levelByte = level switch
        {
            LogLevel.Trace => (byte)0,
            LogLevel.Debug => (byte)1,
            LogLevel.Information => (byte)2,
            LogLevel.Warning => (byte)3,
            LogLevel.Error => (byte)4,
            LogLevel.Critical => (byte)5,
            _ => (byte)2
        };
        var messageSpan = message.AsSpan();
        var totalPayloadLength = 1 + Encoding.UTF8.GetByteCount(messageSpan);
        var poolSize = PooledLogPayloadSize;
        byte[]? rented = null;
        nint unmanaged = 0;

        try
        {
            unsafe
            {
                var buffer = totalPayloadLength <= StackLogPayloadThreshold
                    ? stackalloc byte[StackLogPayloadThreshold]
                    : totalPayloadLength <= poolSize
                        ? (rented = ArrayPool<byte>.Shared.Rent(poolSize)).AsSpan(0, poolSize)
                        : new Span<byte>((void*)(unmanaged = Marshal.AllocHGlobal(BrowserSabInterop.LogChunkSize)),
                            BrowserSabInterop.LogChunkSize);

                if (totalPayloadLength <= buffer.Length)
                {
                    buffer[0] = levelByte;
                    var written = Encoding.UTF8.GetBytes(messageSpan, buffer[1..]);
                    BrowserSabInterop.WriteLogPacketFromMemory(
                        BrowserSabInterop.EventLogMessage,
                        (nint)Unsafe.AsPointer(ref buffer[0]),
                        1 + written,
                        BrowserSabInterop.LogFlagBegin | BrowserSabInterop.LogFlagEnd);
                    return;
                }

                buffer[0] = levelByte;
                BrowserSabInterop.WriteLogPacketFromMemory(
                    BrowserSabInterop.EventLogMessage,
                    (nint)Unsafe.AsPointer(ref buffer[0]),
                    1,
                    BrowserSabInterop.LogFlagBegin);

                var encoder = Encoding.UTF8.GetEncoder();
                var remaining = messageSpan;
                while (!remaining.IsEmpty)
                {
                    encoder.Convert(
                        remaining,
                        buffer,
                        false,
                        out var charsUsed,
                        out var bytesUsed,
                        out _);

                    remaining = remaining[charsUsed..];
                    var isFinal = remaining.IsEmpty;
                    BrowserSabInterop.WriteLogPacketFromMemory(
                        BrowserSabInterop.EventLogMessage,
                        (nint)Unsafe.AsPointer(ref buffer[0]),
                        bytesUsed,
                        isFinal ? BrowserSabInterop.LogFlagEnd : 0);
                }
            }
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
            if (unmanaged != 0)
                Marshal.FreeHGlobal(unmanaged);
        }
    }

    private static string EscapeJson(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }

        return sb.ToString();
    }

    private sealed class BrowserSessionState(PodishContext context, PodishContainerSession session)
    {
        private readonly Lock _outputLock = new();
        private readonly Queue<byte[]> _pendingOutputPackets = [];

        public PodishContext Context { get; } = context;
        public PodishContainerSession Session { get; } = session;
        public BrowserEventDispatcher Dispatcher { get; } = new();
        public int? ExitCode { get; private set; }
        public bool RunStarted { get; private set; }

        public void EnqueueOutput(ReadOnlySpan<byte> payload)
        {
            if (Dispatcher.Emit(BrowserSabQueueKind.Output, BrowserSabInterop.EventOutputBytes, payload) > 0)
            {
                SignalInterrupt(IrqOutputReady);
                return;
            }

            lock (_outputLock)
            {
                _pendingOutputPackets.Enqueue(payload.ToArray());
            }
        }

        public int FlushPendingOutput(int maxPackets)
        {
            if (maxPackets <= 0)
                return 0;

            var flushed = 0;
            while (flushed < maxPackets)
            {
                byte[]? payload;
                lock (_outputLock)
                {
                    if (_pendingOutputPackets.Count == 0)
                        break;
                    payload = _pendingOutputPackets.Peek();
                }

                if (Dispatcher.Emit(BrowserSabQueueKind.Output, BrowserSabInterop.EventOutputBytes, payload) <= 0)
                    break;

                lock (_outputLock)
                {
                    if (_pendingOutputPackets.Count > 0 && ReferenceEquals(_pendingOutputPackets.Peek(), payload))
                        _pendingOutputPackets.Dequeue();
                }

                flushed++;
            }

            if (flushed > 0)
                SignalInterrupt(IrqOutputReady);

            return flushed;
        }

        public async Task RunUntilExitAsync()
        {
            if (RunStarted)
            {
                ExitCode = await Session.WaitAsync();
                return;
            }

            RunStarted = true;
            try
            {
                ExitCode = await Session.WaitAsync();
                EmitExitPacket(ExitCode ?? -1);
            }
            catch
            {
                ExitCode = -1;
                EmitExitPacket(-1);
            }
        }

        private void EmitExitPacket(int exitCode)
        {
            Span<byte> payload = stackalloc byte[5];
            payload[0] = ControlSessionExited;
            BitConverter.TryWriteBytes(payload[1..], exitCode);
            EnqueueOutput(payload);
        }
    }
}
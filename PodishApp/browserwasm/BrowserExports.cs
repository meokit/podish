using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using Fiberish.X86.Native;
using Podish.Core;

namespace PodishApp.BrowserWasm;

[SupportedOSPlatform("browser")]
public static partial class BrowserExports
{
    private static readonly Lock Sync = new();
    private static BrowserSessionState? _session;

    [JSExport]
    public static string GetRuntimeInfo()
    {
        return $"Podish.Core loaded: {typeof(PodishContext).Assembly.GetName().Name}";
    }

    [JSExport]
    public static string ProbeNative()
    {
        var state = X86Native.Create();
        try
        {
            return state == IntPtr.Zero
                ? "X86_Create returned null"
                : $"libfibercpu linked successfully, state=0x{state.ToInt64():x}";
        }
        finally
        {
            if (state != IntPtr.Zero)
                X86Native.Destroy(state);
        }
    }

    [JSExport]
    public static async Task<string> StartRootfsTarShell(byte[] rootfsTarBytes, int rows = 24, int cols = 80)
    {
        if (rootfsTarBytes.Length == 0)
            return Json(ok: false, error: "rootfs tar is empty");

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
            LogLevel = "debug"
        });

        context.SetLogObserver((level, msg) => Console.WriteLine(msg));

        try
        {
            await using var stream = new MemoryStream(rootfsTarBytes, writable: false);
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
                Strace = true,
                Init = false,
                NetworkMode = Fiberish.Core.Net.NetworkMode.Host
            }, rootfsName: "uploaded-rootfs.tar", containerIdOverride: "browserwasm");

            var state = new BrowserSessionState(context, session);
            lock (Sync)
            {
                _session = state;
            }

            _ = state.TrackExitAsync();
            return Json(ok: true, containerId: session.ContainerId, imageRef: session.ImageRef);
        }
        catch (Exception ex)
        {
            context.Dispose();
            return Json(ok: false, error: ex.ToString());
        }
    }

    [JSExport]
    public static byte[] ReadSessionOutput(int maxBytes = 4096)
    {
        var session = GetSession();
        if (session == null || maxBytes <= 0)
            return [];

        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var buffer = pool.Rent(maxBytes);
        try
        {
            var read = session.Session.ReadOutput(buffer.AsSpan(0, maxBytes), 0);
            if (read <= 0)
                return [];

            var result = new byte[read];
            buffer.AsSpan(0, read).CopyTo(result);
            return result;
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    [JSExport]
    public static int WriteSessionInput(byte[] data)
    {
        var session = GetSession();
        return session == null || data.Length == 0 ? 0 : session.Session.WriteInput(data);
    }

    [JSExport]
    public static bool ResizeSessionTerminal(int rows, int cols)
    {
        var session = GetSession();
        if (session == null || rows <= 0 || cols <= 0)
            return false;

        return session.Session.Resize((ushort)rows, (ushort)cols);
    }

    [JSExport]
    public static string GetSessionStatus()
    {
        var session = GetSession();
        if (session == null)
            return Json(hasSession: false, running: false);

        return Json(
            hasSession: true,
            running: !session.ExitCode.HasValue,
            exitCode: session.ExitCode,
            containerId: session.Session.ContainerId);
    }

    [JSExport]
    public static async Task<string> StopSession()
    {
        BrowserSessionState? session;
        lock (Sync)
        {
            session = _session;
            _session = null;
        }

        if (session == null)
            return Json(ok: true, message: "no active session");

        await DisposeSessionAsync(session);
        return Json(ok: true);
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

    private static string EscapeJson(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
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
        }

        return sb.ToString();
    }

    private sealed class BrowserSessionState(PodishContext context, PodishContainerSession session)
    {
        public PodishContext Context { get; } = context;
        public PodishContainerSession Session { get; } = session;
        public int? ExitCode { get; private set; }

        public async Task TrackExitAsync()
        {
            try
            {
                ExitCode = await Session.WaitAsync();
            }
            catch
            {
                ExitCode = -1;
            }
        }
    }
}

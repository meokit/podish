using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Podish.Core;

namespace Podish.Core.Native;

internal sealed class NativeContext
{
    public required PodishContext Context { get; init; }
    private readonly object _errorLock = new();
    private string _lastError = string.Empty;
    private readonly Dictionary<int, string> _lastErrorByThread = [];

    public string GetLastErrorForCurrentThread()
    {
        var tid = Environment.CurrentManagedThreadId;
        lock (_errorLock)
        {
            return _lastErrorByThread.TryGetValue(tid, out var msg) ? msg : _lastError;
        }
    }

    public void SetLastErrorForCurrentThread(string message)
    {
        var safe = message ?? string.Empty;
        var tid = Environment.CurrentManagedThreadId;
        lock (_errorLock)
        {
            _lastError = safe;
            _lastErrorByThread[tid] = safe;
        }
    }
}

internal sealed class NativeContainer
{
    private readonly object _callbackLock = new();
    private unsafe delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> _outputCallback;
    private IntPtr _outputUserData;

    public required NativeContext Owner { get; init; }
    public required PodishContainerSession Session { get; init; }

    public unsafe void SetOutputCallback(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback,
        IntPtr userData)
    {
        lock (_callbackLock)
        {
            _outputCallback = callback;
            _outputUserData = userData;
            Session.SetOutputHandler(callback == null ? null : OnOutput);
        }
    }

    private unsafe void OnOutput(Fiberish.Core.VFS.TTY.TtyEndpointKind kind, byte[] data)
    {
        delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback;
        IntPtr userData;
        lock (_callbackLock)
        {
            callback = _outputCallback;
            userData = _outputUserData;
        }

        if (callback == null || data.Length == 0)
            return;

        fixed (byte* ptr = data)
        {
            callback(userData, kind == Fiberish.Core.VFS.TTY.TtyEndpointKind.Stderr ? 2 : 1, ptr, data.Length);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct PodCtxOptionsNative
{
    public IntPtr WorkDirUtf8;
    public IntPtr LogLevelUtf8;
    public IntPtr LogFileUtf8;
}

public static class PodishNativeApi
{
    private const int PodOk = 0;
    private const int PodEinval = 22;
    private const int PodEnoent = 2;
    private const int PodEinternal = 10000;

    [UnmanagedCallersOnly(EntryPoint = "pod_ctx_create", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodCtxCreate(PodCtxOptionsNative* options, IntPtr* outCtx)
    {
        if (outCtx == null)
            return PodEinval;

        try
        {
            var workDir = PtrToString(options == null ? IntPtr.Zero : options->WorkDirUtf8) ?? Directory.GetCurrentDirectory();
            var logLevel = PtrToString(options == null ? IntPtr.Zero : options->LogLevelUtf8) ?? "warn";
            var logFile = PtrToString(options == null ? IntPtr.Zero : options->LogFileUtf8);

            var ctx = new NativeContext
            {
                Context = new PodishContext(new PodishContextOptions
                {
                    WorkDir = workDir,
                    LogLevel = logLevel,
                    LogFile = logFile
                })
            };

            var handle = GCHandle.Alloc(ctx, GCHandleType.Normal);
            *outCtx = GCHandle.ToIntPtr(handle);
            return PodOk;
        }
        catch (DirectoryNotFoundException ex)
        {
            SetLastError(outCtx, ex.Message);
            return PodEnoent;
        }
        catch (ArgumentException ex)
        {
            SetLastError(outCtx, ex.Message);
            return PodEinval;
        }
        catch (Exception ex)
        {
            SetLastError(outCtx, ex.ToString());
            return PodEinternal;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_ctx_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void PodCtxDestroy(IntPtr ctxHandle)
    {
        if (ctxHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(ctxHandle);
        if (handle.IsAllocated)
            handle.Free();
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_ctx_last_error", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodCtxLastError(IntPtr ctxHandle, byte* buffer, int capacity)
    {
        if (ctxHandle == IntPtr.Zero)
            return 0;

        var ctx = FromHandle(ctxHandle);
        var message = ctx?.GetLastErrorForCurrentThread() ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(message);
        if (buffer == null || capacity <= 0)
            return bytes.Length;

        var copy = Math.Min(bytes.Length, capacity - 1);
        for (var i = 0; i < copy; i++)
            buffer[i] = bytes[i];
        buffer[copy] = 0;
        return copy;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_image_pull", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodImagePull(IntPtr ctxHandle, IntPtr imageRefUtf8)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            var imageRef = PtrToString(imageRefUtf8);
            if (string.IsNullOrWhiteSpace(imageRef))
                return SetErrorAndReturn(ctx, "image ref is required", PodEinval);

            ctx.Context.PullImageAsync(imageRef).GetAwaiter().GetResult();
            return PodOk;
        }
        catch (DirectoryNotFoundException ex)
        {
            return SetErrorAndReturn(ctx, ex.Message, PodEnoent);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_run_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerRunJson(IntPtr ctxHandle, IntPtr runSpecJsonUtf8, int* exitCode)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            var json = PtrToString(runSpecJsonUtf8);
            if (string.IsNullOrWhiteSpace(json))
                return SetErrorAndReturn(ctx, "run spec json is required", PodEinval);

            var spec = JsonSerializer.Deserialize(json, PodishRunSpecJsonContext.Default.PodishRunSpec);
            if (spec == null)
                return SetErrorAndReturn(ctx, "invalid run spec json", PodEinval);

            var result = ctx.Context.RunAsync(spec).GetAwaiter().GetResult();
            if (exitCode != null)
                *exitCode = result.ExitCode;
            return PodOk;
        }
        catch (JsonException ex)
        {
            return SetErrorAndReturn(ctx, ex.Message, PodEinval);
        }
        catch (DirectoryNotFoundException ex)
        {
            return SetErrorAndReturn(ctx, ex.Message, PodEnoent);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_start_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerStartJson(IntPtr ctxHandle, IntPtr runSpecJsonUtf8, IntPtr* outContainer)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null || outContainer == null)
            return PodEinval;

        try
        {
            var json = PtrToString(runSpecJsonUtf8);
            if (string.IsNullOrWhiteSpace(json))
                return SetErrorAndReturn(ctx, "run spec json is required", PodEinval);

            var spec = JsonSerializer.Deserialize(json, PodishRunSpecJsonContext.Default.PodishRunSpec);
            if (spec == null)
                return SetErrorAndReturn(ctx, "invalid run spec json", PodEinval);

            var session = ctx.Context.StartAsync(spec).GetAwaiter().GetResult();
            var container = new NativeContainer
            {
                Owner = ctx,
                Session = session
            };
            var handle = GCHandle.Alloc(container, GCHandleType.Normal);
            *outContainer = GCHandle.ToIntPtr(handle);
            return PodOk;
        }
        catch (JsonException ex)
        {
            return SetErrorAndReturn(ctx, ex.Message, PodEinval);
        }
        catch (DirectoryNotFoundException ex)
        {
            return SetErrorAndReturn(ctx, ex.Message, PodEnoent);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void PodContainerDestroy(IntPtr containerHandle)
    {
        if (containerHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(containerHandle);
        if (handle.IsAllocated)
            handle.Free();
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_set_output_callback", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerSetOutputCallback(IntPtr containerHandle,
        delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback, IntPtr userData)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;
        container.SetOutputCallback(callback, userData);
        return PodOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_write_stdin", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerWriteStdin(IntPtr containerHandle, byte* data, int len, int* written)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null || len < 0)
            return PodEinval;

        try
        {
            if (len == 0 || data == null)
            {
                if (written != null)
                    *written = 0;
                return PodOk;
            }

            var span = new ReadOnlySpan<byte>(data, len);
            var n = container.Session.WriteInput(span);
            if (written != null)
                *written = n;
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_resize", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodContainerResize(IntPtr containerHandle, ushort rows, ushort cols)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            container.Session.Resize(rows, cols);
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_wait", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerWait(IntPtr containerHandle, int* exitCode)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            var rc = container.Session.WaitAsync().GetAwaiter().GetResult();
            if (exitCode != null)
                *exitCode = rc;
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    private static NativeContext? FromHandle(IntPtr handlePtr)
    {
        if (handlePtr == IntPtr.Zero)
            return null;
        try
        {
            var handle = GCHandle.FromIntPtr(handlePtr);
            return handle.Target as NativeContext;
        }
        catch
        {
            return null;
        }
    }

    private static NativeContainer? FromContainerHandle(IntPtr handlePtr)
    {
        if (handlePtr == IntPtr.Zero)
            return null;
        try
        {
            var handle = GCHandle.FromIntPtr(handlePtr);
            return handle.Target as NativeContainer;
        }
        catch
        {
            return null;
        }
    }

    private static int SetErrorAndReturn(NativeContext ctx, string message, int code)
    {
        ctx.SetLastErrorForCurrentThread(message);
        return code;
    }

    private static unsafe void SetLastError(IntPtr* outCtx, string message)
    {
        if (outCtx == null || *outCtx == IntPtr.Zero)
            return;
        var ctx = FromHandle(*outCtx);
        if (ctx != null)
            ctx.SetLastErrorForCurrentThread(message);
    }

    private static unsafe string? PtrToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(ptr);
    }
}

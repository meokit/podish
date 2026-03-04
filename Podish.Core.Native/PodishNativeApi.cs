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

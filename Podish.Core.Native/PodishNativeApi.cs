using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Podish.Core;

namespace Podish.Core.Native;

internal sealed class NativeJob
{
    public required Task<(string? ResultJson, Exception? Error)> Task { get; init; }
}

internal sealed class NativeContext
{
    public required PodishContext Context { get; init; }

    private readonly object _errorLock = new();
    private readonly object _logCallbackLock = new();
    private readonly object _containersLock = new();
    private string _lastError = string.Empty;
    private readonly Dictionary<int, string> _lastErrorByThread = [];
    private readonly HashSet<NativeContainer> _containers = [];

    private unsafe delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> _logCallback;
    private IntPtr _logUserData;
    private readonly object _stateCallbackLock = new();
    private unsafe delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> _stateCallback;
    private IntPtr _stateUserData;

    public void RegisterContainer(NativeContainer container)
    {
        lock (_containersLock)
        {
            _containers.Add(container);
        }
    }

    public bool ContainsContainer(string containerId)
    {
        lock (_containersLock)
        {
            return _containers.Any(c => string.Equals(c.ContainerId, containerId, StringComparison.Ordinal));
        }
    }

    public void UnregisterContainer(NativeContainer container)
    {
        lock (_containersLock)
        {
            _containers.Remove(container);
        }
    }

    public List<NativeContainer> ContainersSnapshot()
    {
        lock (_containersLock)
        {
            return _containers.ToList();
        }
    }

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

    public unsafe void SetLogCallback(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback,
        IntPtr userData)
    {
        lock (_logCallbackLock)
        {
            _logCallback = callback;
            _logUserData = userData;
        }

        Context.SetLogObserver(callback == null ? null : OnLogLine);
    }

    public unsafe void SetContainerStateCallback(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> callback,
        IntPtr userData)
    {
        lock (_stateCallbackLock)
        {
            _stateCallback = callback;
            _stateUserData = userData;
        }
    }

    public unsafe void EmitContainerStateChanged(IReadOnlyList<NativeContainerListItem> snapshot)
    {
        delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> callback;
        IntPtr userData;
        lock (_stateCallbackLock)
        {
            callback = _stateCallback;
            userData = _stateUserData;
        }

        if (callback == null)
            return;

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, PodishNativeJsonContext.Default.ListNativeContainerListItem);
            fixed (byte* ptr = bytes)
            {
                callback(userData, ptr, bytes.Length);
            }
        }
        catch
        {
            // best-effort callback
        }
    }

    private unsafe void OnLogLine(LogLevel level, string line)
    {
        delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback;
        IntPtr userData;
        lock (_logCallbackLock)
        {
            callback = _logCallback;
            userData = _logUserData;
        }

        if (callback == null || string.IsNullOrEmpty(line))
            return;

        var bytes = Encoding.UTF8.GetBytes(line);
        fixed (byte* ptr = bytes)
        {
            callback(userData, (int)level, ptr, bytes.Length);
        }
    }
}

internal sealed class NativeContainer
{
    private readonly object _gate = new();
    private readonly string _logicalId;
    private readonly DateTimeOffset _createdAt;

    private PodishContainerSession? _session;
    private int? _exitCode;
    private bool _removed;
    private string _persistedState;

    private unsafe delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> _outputCallback;
    private IntPtr _outputUserData;

    public required NativeContext Owner { get; init; }
    public required PodishRunSpec Spec { get; init; }

    public NativeContainer()
    {
        _logicalId = Guid.NewGuid().ToString("N")[..12];
        _createdAt = DateTimeOffset.UtcNow;
        _persistedState = "created";
    }

    public NativeContainer(string containerId, DateTimeOffset createdAt, string state, int? exitCode)
    {
        _logicalId = containerId;
        _createdAt = createdAt;
        _persistedState = string.IsNullOrWhiteSpace(state) ? "created" : state;
        _exitCode = exitCode;
    }

    public string ContainerId
    {
        get
        {
            return _logicalId;
        }
    }

    public string ImageRef
    {
        get
        {
            lock (_gate)
            {
                if (_session != null) return _session.ImageRef;
                return Spec.Image ?? Spec.Rootfs ?? string.Empty;
            }
        }
    }

    public bool HasTerminal
    {
        get
        {
            lock (_gate)
            {
                return _session?.HasTerminal ?? (Spec.Interactive && Spec.Tty);
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _session != null && !_session.IsCompleted;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exitCode;
            }
        }
    }

    public string State
    {
        get
        {
            lock (_gate)
            {
                if (_session == null) return _persistedState;
                if (_session.IsCompleted) return "exited";
                return "running";
            }
        }
    }

    public async Task StartAsync()
    {
        PodishContainerSession? current;
        lock (_gate)
        {
            if (_removed)
                throw new InvalidOperationException("container is removed");
            current = _session;
        }

        if (current is { IsCompleted: false })
            return;

        var session = await Owner.Context.StartAsync(Spec, _logicalId);
        lock (_gate)
        {
            _session = session;
            _exitCode = null;
            _persistedState = "created";
            ApplyOutputCallbackLocked();
            PersistMetadataLocked("created");
        }

        var started = await WaitUntilStartedOrExitedAsync(session, timeoutMs: 3000);
        if (!started)
        {
            var rc = await session.WaitAsync();
            MarkExited(session, rc);
            throw new InvalidOperationException($"container failed to start (exit={rc})");
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
                return;
            _persistedState = "running";
            PersistMetadataLocked("running");
        }
        NotifyContainerStateChanged();
        _ = ObserveExitAsync(session);
    }

    public async Task<int> WaitAsync()
    {
        PodishContainerSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session == null)
            throw new InvalidOperationException("container is not started");

        var rc = await session.WaitAsync();
        MarkExited(session, rc);
        return rc;
    }

    public bool Stop(int signal, int timeoutMs)
    {
        PodishContainerSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session == null || session.IsCompleted)
            return true;

        if (signal <= 0)
            signal = 15; // SIGTERM

        session.SignalInitProcess(signal);

        if (timeoutMs <= 0)
            return true;

        try
        {
            if (session.WaitAsync().Wait(TimeSpan.FromMilliseconds(timeoutMs)))
                return true;

            // Escalate to SIGKILL after graceful timeout.
            session.SignalInitProcess(9);
            var killWaitMs = Math.Max(250, timeoutMs);
            return session.WaitAsync().Wait(TimeSpan.FromMilliseconds(killWaitMs));
        }
        catch
        {
            return false;
        }
    }

    public int ReadOutput(Span<byte> buffer, int timeoutMs)
    {
        PodishContainerSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session == null)
            return 0;

        return session.ReadOutput(buffer, timeoutMs);
    }

    public int WriteInput(ReadOnlySpan<byte> data)
    {
        PodishContainerSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session == null)
            return 0;

        return session.WriteInput(data);
    }

    public bool Resize(ushort rows, ushort cols)
    {
        PodishContainerSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session == null)
            return false;

        return session.Resize(rows, cols);
    }

    public unsafe void SetOutputCallback(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback,
        IntPtr userData)
    {
        lock (_gate)
        {
            _outputCallback = callback;
            _outputUserData = userData;
            ApplyOutputCallbackLocked();
        }
    }

    private unsafe void ApplyOutputCallbackLocked()
    {
        if (_session == null)
            return;

        if (_outputCallback == null)
        {
            _session.SetOutputHandler(null);
            return;
        }

        _session.SetOutputHandler((kind, data) =>
        {
            if (data.Length == 0)
                return;
            fixed (byte* ptr = data)
            {
                _outputCallback(_outputUserData,
                    kind == Fiberish.Core.VFS.TTY.TtyEndpointKind.Stderr ? 2 : 1,
                    ptr,
                    data.Length);
            }
        });
    }

    public void InitializeMetadata()
    {
        lock (_gate)
        {
            _persistedState = "created";
            PersistMetadataLocked("created");
        }
    }

    public void DeleteMetadataAndData()
    {
        lock (_gate)
        {
            _removed = true;
        }

        var dir = ContainerDir;
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    public NativeContainerMetadata BuildMetadataSnapshot()
    {
        lock (_gate)
        {
            return new NativeContainerMetadata(
                ContainerId: _logicalId,
                Image: ImageRef,
                State: State,
                HasTerminal: HasTerminal,
                Running: IsRunning,
                ExitCode: _exitCode,
                CreatedAt: _createdAt,
                UpdatedAt: DateTimeOffset.UtcNow,
                Spec: Spec);
        }
    }

    private string ContainerDir => Path.Combine(Owner.Context.ContainersDir, _logicalId);
    private string MetadataPath => Path.Combine(ContainerDir, "container.json");

    private void PersistMetadataLocked(string forcedState)
    {
        Directory.CreateDirectory(ContainerDir);
        var metadata = new NativeContainerMetadata(
            ContainerId: _logicalId,
            Image: ImageRef,
            State: forcedState,
            HasTerminal: HasTerminal,
            Running: forcedState == "running",
            ExitCode: _exitCode,
            CreatedAt: _createdAt,
            UpdatedAt: DateTimeOffset.UtcNow,
            Spec: Spec);
        var json = JsonSerializer.Serialize(metadata, PodishNativeJsonContext.Default.NativeContainerMetadata);
        File.WriteAllText(MetadataPath, json);
    }

    private async Task ObserveExitAsync(PodishContainerSession session)
    {
        try
        {
            var rc = await session.WaitAsync();
            MarkExited(session, rc);
        }
        catch
        {
            // ignore observer failures
        }
    }

    private void MarkExited(PodishContainerSession session, int rc)
    {
        var changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
                return;
            if (_persistedState == "exited" && _exitCode == rc)
                return;
            _exitCode = rc;
            _persistedState = "exited";
            PersistMetadataLocked("exited");
            changed = true;
        }

        if (changed)
            NotifyContainerStateChanged();
    }

    private static async Task<bool> WaitUntilStartedOrExitedAsync(PodishContainerSession session, int timeoutMs)
    {
        var elapsed = 0;
        const int stepMs = 10;
        while (elapsed < timeoutMs)
        {
            if (session.InitPid.HasValue)
                return true;
            if (session.IsCompleted)
                return false;
            await Task.Delay(stepMs);
            elapsed += stepMs;
        }

        // Timeout fallback: if process is still alive, treat as started.
        return !session.IsCompleted;
    }

    private void NotifyContainerStateChanged()
    {
        var snapshot = PodishNativeApi.BuildContainerListSnapshot(Owner);
        Owner.EmitContainerStateChanged(snapshot);
    }
}

internal sealed class NativeTerminal
{
    public required NativeContainer Container { get; init; }
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
    private const int PodEbusy = 16;
    private const int PodEinternal = 10000;

    [UnmanagedCallersOnly(EntryPoint = "pod_ctx_create", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodCtxCreate(PodCtxOptionsNative* options, IntPtr* outCtx)
    {
        if (outCtx == null)
            return PodEinval;

        try
        {
            var workDir = PtrToString(options == null ? IntPtr.Zero : options->WorkDirUtf8) ??
                          Directory.GetCurrentDirectory();
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
    public static unsafe void PodCtxDestroy(IntPtr ctxHandle)
    {
        if (ctxHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(ctxHandle);
        if (!handle.IsAllocated)
            return;

        if (handle.Target is NativeContext nativeContext)
        {
            nativeContext.SetLogCallback(null, IntPtr.Zero);
            nativeContext.Context.Dispose();
        }

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

    [UnmanagedCallersOnly(EntryPoint = "pod_ctx_set_log_callback", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodCtxSetLogCallback(IntPtr ctxHandle,
        delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback, IntPtr userData)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            ctx.SetLogCallback(callback, userData);
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_ctx_set_container_state_callback", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodCtxSetContainerStateCallback(IntPtr ctxHandle,
        delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> callback, IntPtr userData)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            ctx.SetContainerStateCallback(callback, userData);
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
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

    [UnmanagedCallersOnly(EntryPoint = "pod_image_pull_async", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodImagePullAsync(IntPtr ctxHandle, IntPtr imageRefUtf8, IntPtr* outJob)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null || outJob == null)
            return PodEinval;

        var imageRef = PtrToString(imageRefUtf8);
        if (string.IsNullOrWhiteSpace(imageRef))
            return SetErrorAndReturn(ctx, "image ref is required", PodEinval);

        var nativeJob = new NativeJob
        {
            Task = Task.Run(() =>
            {
                try
                {
                    ctx.Context.PullImageAsync(imageRef).GetAwaiter().GetResult();
                    var result = "{\"image\":\"" + JsonEncodedText.Encode(imageRef).ToString() + "\"}";
                    return (result, (Exception?)null);
                }
                catch (Exception ex)
                {
                    return ((string?)null, ex);
                }
            })
        };

        var handle = GCHandle.Alloc(nativeJob, GCHandleType.Normal);
        *outJob = GCHandle.ToIntPtr(handle);
        return PodOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_image_list_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodImageListJson(IntPtr ctxHandle, byte* buffer, int capacity, int* outLen)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            var list = ListImages(ctx.Context);
            return WriteJson(ctx, list, PodishNativeJsonContext.Default.ListNativeImageListItem, buffer, capacity, outLen);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_image_remove", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodImageRemove(IntPtr ctxHandle, IntPtr imageRefUtf8, int force)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            var imageRef = PtrToString(imageRefUtf8);
            if (string.IsNullOrWhiteSpace(imageRef))
                return SetErrorAndReturn(ctx, "image ref is required", PodEinval);

            var safe = imageRef.Replace("/", "_").Replace(":", "_");
            var dir = Path.Combine(ctx.Context.OciStoreImagesDir, safe);
            if (!Directory.Exists(dir))
                return PodEnoent;
            Directory.Delete(dir, true);
            return PodOk;
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
            if (!TryParseRunSpec(ctx, runSpecJsonUtf8, out var spec, out var err))
                return SetErrorAndReturn(ctx, err, PodEinval);

            var result = ctx.Context.RunAsync(spec!).GetAwaiter().GetResult();
            if (exitCode != null)
                *exitCode = result.ExitCode;
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_create_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerCreateJson(IntPtr ctxHandle, IntPtr runSpecJsonUtf8, IntPtr* outContainer)
    {
        return CreateContainerFromJson(ctxHandle, runSpecJsonUtf8, outContainer);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_open", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerOpen(IntPtr ctxHandle, IntPtr containerIdUtf8, IntPtr* outContainer)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null || outContainer == null)
            return PodEinval;

        var containerId = PtrToString(containerIdUtf8);
        if (string.IsNullOrWhiteSpace(containerId))
            return SetErrorAndReturn(ctx, "container id is required", PodEinval);

        try
        {
            var container = OpenContainerById(ctx, containerId);
            if (container == null)
                return SetErrorAndReturn(ctx, $"container not found: {containerId}", PodEnoent);

            var handle = GCHandle.Alloc(container, GCHandleType.Normal);
            *outContainer = GCHandle.ToIntPtr(handle);
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_start", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodContainerStart(IntPtr containerHandle)
    {
        return StartContainer(containerHandle);
    }

    private static unsafe int CreateContainerFromJson(IntPtr ctxHandle, IntPtr runSpecJsonUtf8, IntPtr* outContainer)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null || outContainer == null)
            return PodEinval;

        try
        {
            if (!TryParseRunSpec(ctx, runSpecJsonUtf8, out var spec, out var err))
                return SetErrorAndReturn(ctx, err, PodEinval);

            var container = new NativeContainer
            {
                Owner = ctx,
                Spec = spec!
            };
            container.InitializeMetadata();
            ctx.RegisterContainer(container);
            var handle = GCHandle.Alloc(container, GCHandleType.Normal);
            *outContainer = GCHandle.ToIntPtr(handle);
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    private static int StartContainer(IntPtr containerHandle)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            container.StartAsync().GetAwaiter().GetResult();
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_start_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerStartJson(IntPtr ctxHandle, IntPtr runSpecJsonUtf8, IntPtr* outContainer)
    {
        var rc = CreateContainerFromJson(ctxHandle, runSpecJsonUtf8, outContainer);
        if (rc != PodOk)
            return rc;
        return StartContainer(*outContainer);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_list_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerListJson(IntPtr ctxHandle, byte* buffer, int capacity, int* outLen)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            var ordered = BuildContainerListSnapshot(ctx);
            return WriteJson(ctx, ordered, PodishNativeJsonContext.Default.ListNativeContainerListItem, buffer, capacity,
                outLen);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_inspect_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerInspectJson(IntPtr containerHandle, byte* buffer, int capacity, int* outLen)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            var inspect = new NativeContainerInspect(
                Handle: container.GetHashCode().ToString("x"),
                ContainerId: container.ContainerId,
                Image: container.ImageRef,
                State: container.State,
                HasTerminal: container.HasTerminal,
                Running: container.IsRunning,
                ExitCode: container.ExitCode,
                Spec: container.Spec);

            return WriteJson(container.Owner, inspect, PodishNativeJsonContext.Default.NativeContainerInspect, buffer,
                capacity, outLen);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_stop", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodContainerStop(IntPtr containerHandle, int signal, int timeoutMs)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            return container.Stop(signal, timeoutMs) ? PodOk : PodEbusy;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_remove", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodContainerRemove(IntPtr containerHandle, int force)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        if (container.IsRunning && force == 0)
            return PodEbusy;

        if (force != 0 && container.IsRunning)
            container.Stop(15, 1000);

        try
        {
            container.DeleteMetadataAndData();
            container.Owner.UnregisterContainer(container);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }

        return PodOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void PodContainerDestroy(IntPtr containerHandle)
    {
        if (containerHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(containerHandle);
        if (!handle.IsAllocated)
            return;

        if (handle.Target is NativeContainer container)
            container.Owner.UnregisterContainer(container);

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
            var n = container.WriteInput(span);
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
            return container.Resize(rows, cols) ? PodOk : PodEinval;
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
            var rc = container.WaitAsync().GetAwaiter().GetResult();
            if (exitCode != null)
                *exitCode = rc;
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_wait_async", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodContainerWaitAsync(IntPtr containerHandle, IntPtr* outJob)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null || outJob == null)
            return PodEinval;

        var nativeJob = new NativeJob
        {
            Task = Task.Run(() =>
            {
                try
                {
                    var rc = container.WaitAsync().GetAwaiter().GetResult();
                    var result = "{\"exitCode\":" + rc + "}";
                    return (result, (Exception?)null);
                }
                catch (Exception ex)
                {
                    return ((string?)null, ex);
                }
            })
        };

        var handle = GCHandle.Alloc(nativeJob, GCHandleType.Normal);
        *outJob = GCHandle.ToIntPtr(handle);
        return PodOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_terminal_attach", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodTerminalAttach(IntPtr containerHandle, IntPtr* outTerminal)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null || outTerminal == null)
            return PodEinval;

        if (!container.IsRunning)
            return SetErrorAndReturn(container.Owner, "container is not running", PodEbusy);

        if (!container.HasTerminal)
            return SetErrorAndReturn(container.Owner, "container has no terminal", PodEinval);

        var terminal = new NativeTerminal { Container = container };
        var handle = GCHandle.Alloc(terminal, GCHandleType.Normal);
        *outTerminal = GCHandle.ToIntPtr(handle);
        return PodOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_terminal_write", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodTerminalWrite(IntPtr terminalHandle, byte* data, int len, int* written)
    {
        var terminal = FromTerminalHandle(terminalHandle);
        if (terminal == null || len < 0)
            return PodEinval;

        if (len == 0 || data == null)
        {
            if (written != null)
                *written = 0;
            return PodOk;
        }

        try
        {
            var n = terminal.Container.WriteInput(new ReadOnlySpan<byte>(data, len));
            if (written != null)
                *written = n;
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(terminal.Container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_terminal_read", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodTerminalRead(IntPtr terminalHandle, byte* buffer, int capacity, int timeoutMs,
        int* outRead)
    {
        var terminal = FromTerminalHandle(terminalHandle);
        if (terminal == null || capacity < 0 || buffer == null || outRead == null)
            return PodEinval;

        try
        {
            var span = new Span<byte>(buffer, capacity);
            var n = terminal.Container.ReadOutput(span, timeoutMs);
            *outRead = n;
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(terminal.Container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_terminal_resize", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodTerminalResize(IntPtr terminalHandle, ushort rows, ushort cols)
    {
        var terminal = FromTerminalHandle(terminalHandle);
        if (terminal == null)
            return PodEinval;

        try
        {
            return terminal.Container.Resize(rows, cols) ? PodOk : PodEinval;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(terminal.Container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_terminal_close", CallConvs = [typeof(CallConvCdecl)])]
    public static void PodTerminalClose(IntPtr terminalHandle)
    {
        if (terminalHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(terminalHandle);
        if (handle.IsAllocated)
            handle.Free();
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_events_read_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodEventsReadJson(IntPtr ctxHandle, IntPtr cursorUtf8, int timeoutMs,
        byte* buffer, int capacity, int* outLen)
    {
        var ctx = FromHandle(ctxHandle);
        if (ctx == null)
            return PodEinval;

        try
        {
            var cursor = PtrToString(cursorUtf8);
            var offset = 0;
            if (!string.IsNullOrWhiteSpace(cursor))
                int.TryParse(cursor, out offset);

            var store = new ContainerEventStore(Path.Combine(ctx.Context.FiberpodDir, "events.jsonl"));
            var all = store.ReadAll().ToList();
            if (offset < 0) offset = 0;
            if (offset > all.Count) offset = all.Count;

            var chunk = new NativeEventsChunk((all.Count).ToString(), all.Skip(offset).Take(256).ToList());
            return WriteJson(ctx, chunk, PodishNativeJsonContext.Default.NativeEventsChunk, buffer, capacity, outLen);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_logs_read_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodLogsReadJson(IntPtr containerHandle, IntPtr cursorUtf8, int follow, int timeoutMs,
        byte* buffer, int capacity, int* outLen)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            var cursor = PtrToString(cursorUtf8);
            var offset = 0;
            if (!string.IsNullOrWhiteSpace(cursor))
                int.TryParse(cursor, out offset);
            if (offset < 0) offset = 0;

            var logPath = Path.Combine(container.Owner.Context.ContainersDir, container.ContainerId, "ctr.log");
            var started = DateTime.UtcNow;
            List<ContainerLogEntry> entries = [];
            var nextCursor = offset;

            while (true)
            {
                entries = ReadContainerLogs(logPath, offset, 256, out nextCursor);
                if (entries.Count > 0 || follow == 0)
                    break;

                if (timeoutMs == 0)
                    break;
                if (timeoutMs > 0 && (DateTime.UtcNow - started).TotalMilliseconds >= timeoutMs)
                    break;
                Thread.Sleep(100);
            }

            var chunk = new NativeLogsChunk(nextCursor.ToString(), entries);
            return WriteJson(container.Owner, chunk, PodishNativeJsonContext.Default.NativeLogsChunk, buffer, capacity,
                outLen);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_job_poll_json", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int PodJobPollJson(IntPtr jobHandle, byte* buffer, int capacity, int* outLen)
    {
        var job = FromJobHandle(jobHandle);
        if (job == null)
            return PodEinval;

        NativeJobPollResponse response;
        if (!job.Task.IsCompleted)
        {
            response = new NativeJobPollResponse("running", null, null);
        }
        else
        {
            var (resultJson, error) = job.Task.GetAwaiter().GetResult();
            response = error == null
                ? new NativeJobPollResponse("succeeded", null, resultJson)
                : new NativeJobPollResponse("failed", error.ToString(), null);
        }

        return WriteJsonWithoutContext(response, PodishNativeJsonContext.Default.NativeJobPollResponse, buffer,
            capacity, outLen);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_job_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void PodJobDestroy(IntPtr jobHandle)
    {
        if (jobHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(jobHandle);
        if (handle.IsAllocated)
            handle.Free();
    }

    private static List<NativeImageListItem> ListImages(PodishContext context)
    {
        var result = new List<NativeImageListItem>();
        if (!Directory.Exists(context.OciStoreImagesDir))
            return result;

        foreach (var dir in Directory.GetDirectories(context.OciStoreImagesDir))
        {
            var imagePath = Path.Combine(dir, "image.json");
            if (!File.Exists(imagePath))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(imagePath));
                var root = doc.RootElement;
                var imageRef = root.TryGetProperty("ImageReference", out var ir)
                    ? ir.GetString() ?? Path.GetFileName(dir)
                    : Path.GetFileName(dir);
                var digest = root.TryGetProperty("ManifestDigest", out var dg) ? dg.GetString() ?? string.Empty :
                    string.Empty;
                var repository = root.TryGetProperty("Repository", out var rp) ? rp.GetString() : null;
                var tag = root.TryGetProperty("Tag", out var tg) ? tg.GetString() : null;
                var layerCount = root.TryGetProperty("Layers", out var layers) && layers.ValueKind == JsonValueKind.Array
                    ? layers.GetArrayLength()
                    : 0;

                result.Add(new NativeImageListItem(imageRef, digest, layerCount, dir, tag, repository));
            }
            catch
            {
                // ignore malformed entries
            }
        }

        return result;
    }

    internal static List<NativeContainerListItem> BuildContainerListSnapshot(NativeContext ctx)
    {
        var list = ReadPersistedContainerMetadata(ctx.Context.ContainersDir)
            .Select(m => new NativeContainerListItem(
                Handle: string.Empty,
                ContainerId: m.ContainerId,
                Image: m.Image,
                State: string.Equals(m.State, "running", StringComparison.OrdinalIgnoreCase) ? "exited" : m.State,
                HasTerminal: m.HasTerminal,
                Running: false,
                ExitCode: m.ExitCode))
            .ToDictionary(x => x.ContainerId, x => x, StringComparer.Ordinal);

        foreach (var live in ctx.ContainersSnapshot())
        {
            var item = new NativeContainerListItem(
                Handle: live.GetHashCode().ToString("x"),
                ContainerId: live.ContainerId,
                Image: live.ImageRef,
                State: live.State,
                HasTerminal: live.HasTerminal,
                Running: live.IsRunning,
                ExitCode: live.ExitCode);
            list[live.ContainerId] = item;
        }

        return list.Values.OrderByDescending(x => x.Running).ThenBy(x => x.ContainerId).ToList();
    }

    private static NativeContainer? OpenContainerById(NativeContext ctx, string containerId)
    {
        var live = ctx.ContainersSnapshot().FirstOrDefault(c => string.Equals(c.ContainerId, containerId, StringComparison.Ordinal));
        if (live != null)
            return live;

        var metadata = ReadPersistedContainerMetadata(ctx.Context.ContainersDir)
            .FirstOrDefault(m => string.Equals(m.ContainerId, containerId, StringComparison.Ordinal));
        if (metadata == null)
            return null;

        var container = new NativeContainer(metadata.ContainerId, metadata.CreatedAt, metadata.State, metadata.ExitCode)
        {
            Owner = ctx,
            Spec = metadata.Spec
        };
        if (!ctx.ContainsContainer(container.ContainerId))
            ctx.RegisterContainer(container);
        return container;
    }

    private static List<NativeContainerMetadata> ReadPersistedContainerMetadata(string containersDir)
    {
        var result = new List<NativeContainerMetadata>();
        if (!Directory.Exists(containersDir))
            return result;

        foreach (var dir in Directory.GetDirectories(containersDir))
        {
            var path = Path.Combine(dir, "container.json");
            if (!File.Exists(path))
                continue;
            try
            {
                var metadata =
                    JsonSerializer.Deserialize(File.ReadAllText(path), PodishNativeJsonContext.Default.NativeContainerMetadata);
                if (metadata != null)
                    result.Add(metadata);
            }
            catch
            {
                // ignore malformed metadata
            }
        }

        return result;
    }

    private static List<ContainerLogEntry> ReadContainerLogs(string logPath, int offset, int maxEntries, out int nextCursor)
    {
        nextCursor = offset;
        var result = new List<ContainerLogEntry>();
        if (!File.Exists(logPath))
            return result;

        var idx = 0;
        foreach (var line in File.ReadLines(logPath))
        {
            if (idx < offset)
            {
                idx++;
                continue;
            }

            if (result.Count >= maxEntries)
                break;

            if (string.IsNullOrWhiteSpace(line))
            {
                idx++;
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var time = root.TryGetProperty("Time", out var t)
                    ? (t.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(t.GetString(), out var parsed)
                        ? parsed
                        : DateTimeOffset.UtcNow)
                    : DateTimeOffset.UtcNow;
                var stream = root.TryGetProperty("Stream", out var s) ? (s.GetString() ?? "stdout") : "stdout";
                var log = root.TryGetProperty("Log", out var l) ? (l.GetString() ?? string.Empty) : string.Empty;
                result.Add(new ContainerLogEntry(time, stream, log));
            }
            catch
            {
                // ignore malformed line
            }

            idx++;
        }

        nextCursor = idx;
        return result;
    }

    private static bool TryParseRunSpec(NativeContext ctx, IntPtr runSpecJsonUtf8, out PodishRunSpec? spec,
        out string error)
    {
        spec = null;
        error = string.Empty;
        var json = PtrToString(runSpecJsonUtf8);
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "run spec json is required";
            return false;
        }

        try
        {
            spec = JsonSerializer.Deserialize(json, PodishNativeJsonContext.Default.PodishRunSpec);
            if (spec == null)
            {
                error = "invalid run spec json";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
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

    private static NativeTerminal? FromTerminalHandle(IntPtr handlePtr)
    {
        if (handlePtr == IntPtr.Zero)
            return null;
        try
        {
            var handle = GCHandle.FromIntPtr(handlePtr);
            return handle.Target as NativeTerminal;
        }
        catch
        {
            return null;
        }
    }

    private static NativeJob? FromJobHandle(IntPtr handlePtr)
    {
        if (handlePtr == IntPtr.Zero)
            return null;
        try
        {
            var handle = GCHandle.FromIntPtr(handlePtr);
            return handle.Target as NativeJob;
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

    private static unsafe int WriteJson<T>(NativeContext ctx, T payload, JsonTypeInfo<T> typeInfo,
        byte* buffer, int capacity, int* outLen)
    {
        try
        {
            return WriteJsonWithoutContext(payload, typeInfo, buffer, capacity, outLen);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(ctx, ex.ToString(), PodEinternal);
        }
    }

    private static unsafe int WriteJsonWithoutContext<T>(T payload, JsonTypeInfo<T> typeInfo,
        byte* buffer, int capacity, int* outLen)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        if (outLen != null)
            *outLen = bytes.Length;

        if (buffer == null || capacity <= 0)
            return PodOk;

        var copy = Math.Min(bytes.Length, capacity - 1);
        for (var i = 0; i < copy; i++)
            buffer[i] = bytes[i];
        buffer[copy] = 0;
        return bytes.Length < capacity ? PodOk : PodEinval;
    }

    private static unsafe string? PtrToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(ptr);
    }
}

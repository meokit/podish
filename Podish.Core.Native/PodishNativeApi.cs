using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Podish.Core;

namespace Podish.Core.Native;

internal sealed class NativeJob
{
    public required Task<(string? ResultJson, Exception? Error)> Task { get; init; }
}

internal sealed class NativeContext : IDisposable
{
    public required PodishContext Context { get; init; }

    private readonly Channel<Func<NativeContext, ValueTask>> _commandQueue =
        Channel.CreateUnbounded<Func<NativeContext, ValueTask>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Task _runtimeLoop;
    private int _disposed;

    private static readonly AsyncLocal<NativeContext?> RuntimeScope = new();
    private string _lastError = string.Empty;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _lastErrorByThread = [];

    private readonly Dictionary<string, NativeContainer> _containersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _containerIdByName = new(StringComparer.Ordinal);

    private unsafe NativeLogCallbackState? _logCallbackState;
    private unsafe NativeContainerStateCallbackState? _stateCallbackState;

    public NativeContext()
    {
        _runtimeLoop = Task.Run(ProcessCommandsAsync);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _commandQueue.Writer.TryComplete();
        _runtimeLoop.GetAwaiter().GetResult();
    }

    public NativeContainer RegisterOrGetContainer(NativeContainer container)
    {
        return Invoke(ctx => ctx.RegisterOrGetContainerUnsafe(container));
    }

    public void RegisterContainer(NativeContainer container)
    {
        RegisterOrGetContainer(container);
    }

    public bool ContainsContainer(string containerId)
    {
        return Invoke(ctx => ctx._containersById.ContainsKey(containerId));
    }

    public void UnregisterContainer(NativeContainer container)
    {
        Invoke(ctx => ctx.UnregisterContainerUnsafe(container));
    }

    public List<NativeContainer> ContainersSnapshot()
    {
        return Invoke(ctx => ctx._containersById.Values.ToList());
    }

    public NativeContainer? OpenContainerByIdOrName(string query)
    {
        return Invoke(ctx => ctx.OpenContainerByIdOrNameUnsafe(query));
    }

    public (NativeContainer? Container, string? Error, int Code) CreateContainer(PodishRunSpec spec)
    {
        return Invoke(ctx => ctx.CreateContainerUnsafe(spec));
    }

    public (bool Ok, string? Error) TryRenameContainer(NativeContainer container, string? newName)
    {
        return Invoke(ctx => ctx.TryRenameContainerUnsafe(container, newName));
    }

    public string GetLastErrorForCurrentThread()
    {
        var tid = Environment.CurrentManagedThreadId;
        return _lastErrorByThread.TryGetValue(tid, out var msg) ? msg : Volatile.Read(ref _lastError);
    }

    public void SetLastErrorForCurrentThread(string message)
    {
        var safe = message ?? string.Empty;
        Volatile.Write(ref _lastError, safe);
        _lastErrorByThread[Environment.CurrentManagedThreadId] = safe;
    }

    public unsafe void SetLogCallback(delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback,
        IntPtr userData)
    {
        var next = callback == null ? null : new NativeLogCallbackState(callback, userData);
        var prev = Interlocked.Exchange(ref _logCallbackState, next);
        Context.SetLogObserver(next == null ? null : OnLogLine);
        prev?.RetireAndWait();
    }

    public unsafe void SetContainerStateCallback(delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> callback,
        IntPtr userData)
    {
        var next = callback == null ? null : new NativeContainerStateCallbackState(callback, userData);
        var prev = Interlocked.Exchange(ref _stateCallbackState, next);
        prev?.RetireAndWait();
    }

    public unsafe void EmitContainerStateChanged(IReadOnlyList<NativeContainerListItem> snapshot)
    {
        var state = Volatile.Read(ref _stateCallbackState);
        if (state == null)
            return;

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot,
                PodishNativeJsonContext.Default.ListNativeContainerListItem);
            state.Invoke(bytes);
        }
        catch
        {
            // best-effort callback
        }
    }

    private async Task ProcessCommandsAsync()
    {
        await foreach (var command in _commandQueue.Reader.ReadAllAsync())
        {
            var previous = RuntimeScope.Value;
            RuntimeScope.Value = this;
            try
            {
                await command(this);
            }
            finally
            {
                RuntimeScope.Value = previous;
            }
        }
    }

    private Task<T> InvokeAsync<T>(Func<NativeContext, ValueTask<T>> action)
    {
        if (ReferenceEquals(RuntimeScope.Value, this))
        {
            try
            {
                return action(this).AsTask();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(NativeContext)));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = _commandQueue.Writer.TryWrite(async ctx =>
        {
            try
            {
                var result = await action(ctx);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (!accepted)
            tcs.SetException(new ObjectDisposedException(nameof(NativeContext)));
        return tcs.Task;
    }

    private T Invoke<T>(Func<NativeContext, T> action)
    {
        return InvokeAsync(ctx => ValueTask.FromResult(action(ctx))).GetAwaiter().GetResult();
    }

    private void Invoke(Action<NativeContext> action)
    {
        InvokeAsync(ctx =>
        {
            action(ctx);
            return ValueTask.FromResult(0);
        }).GetAwaiter().GetResult();
    }

    private NativeContainer RegisterOrGetContainerUnsafe(NativeContainer container)
    {
        if (_containersById.TryGetValue(container.ContainerId, out var existing))
            return existing;

        _containersById[container.ContainerId] = container;
        IndexContainerNameUnsafe(container);
        return container;
    }

    private void UnregisterContainerUnsafe(NativeContainer container)
    {
        if (!_containersById.Remove(container.ContainerId))
            return;

        RemoveNameMappingsUnsafe(container.ContainerId);
    }

    private NativeContainer? OpenContainerByIdOrNameUnsafe(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var live = FindLiveContainerUnsafe(query);
        if (live != null)
            return live;

        var metadata = PodishContainerMetadataStore.Resolve(Context.ContainersDir, query);
        if (metadata == null)
            return null;

        if (_containersById.TryGetValue(metadata.ContainerId, out var existing))
            return existing;

        var container = new NativeContainer(
            metadata.ContainerId,
            metadata.Name,
            metadata.CreatedAt,
            metadata.State,
            metadata.ExitCode)
        {
            Owner = this,
            Spec = metadata.Spec
        };

        _containersById[container.ContainerId] = container;
        IndexContainerNameUnsafe(container);
        return container;
    }

    private (NativeContainer? Container, string? Error, int Code) CreateContainerUnsafe(PodishRunSpec spec)
    {
        if (!PodishContainerMetadataStore.IsValidName(spec.Name))
            return (null, "invalid container name", PodishNativeApi.PodEinval);

        if (!IsNameAvailableUnsafe(spec.Name, excludeContainerId: null))
            return (null, "container name already exists", PodishNativeApi.PodEbusy);

        var container = new NativeContainer
        {
            Owner = this,
            Spec = spec
        };
        container.InitializeMetadata();
        _containersById[container.ContainerId] = container;
        IndexContainerNameUnsafe(container);
        return (container, null, PodishNativeApi.PodOk);
    }

    private (bool Ok, string? Error) TryRenameContainerUnsafe(NativeContainer container, string? newName)
    {
        if (!IsNameAvailableUnsafe(newName, container.ContainerId))
            return (false, "container name already exists");

        RemoveNameMappingsUnsafe(container.ContainerId);
        container.Rename(newName);
        IndexContainerNameUnsafe(container);
        return (true, null);
    }

    private NativeContainer? FindLiveContainerUnsafe(string query)
    {
        if (_containersById.TryGetValue(query, out var byId))
            return byId;

        if (_containerIdByName.TryGetValue(query, out var containerId) &&
            _containersById.TryGetValue(containerId, out var byIndexedName))
        {
            return byIndexedName;
        }

        return _containersById.Values.FirstOrDefault(c => string.Equals(c.Name, query, StringComparison.Ordinal));
    }

    private bool IsNameAvailableUnsafe(string? candidateName, string? excludeContainerId)
    {
        if (string.IsNullOrWhiteSpace(candidateName))
            return true;

        if (_containerIdByName.TryGetValue(candidateName, out var liveId) &&
            !string.Equals(liveId, excludeContainerId, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var metadata in PodishContainerMetadataStore.ReadAll(Context.ContainersDir))
        {
            if (!string.Equals(metadata.Name, candidateName, StringComparison.Ordinal))
                continue;
            if (string.Equals(metadata.ContainerId, excludeContainerId, StringComparison.Ordinal))
                continue;
            return false;
        }

        return true;
    }

    private void IndexContainerNameUnsafe(NativeContainer container)
    {
        var name = container.Name;
        if (!string.IsNullOrWhiteSpace(name))
            _containerIdByName[name] = container.ContainerId;
    }

    private void RemoveNameMappingsUnsafe(string containerId)
    {
        var staleNames = _containerIdByName
            .Where(x => string.Equals(x.Value, containerId, StringComparison.Ordinal))
            .Select(x => x.Key)
            .ToList();
        foreach (var name in staleNames)
            _containerIdByName.Remove(name);
    }

    private unsafe void OnLogLine(LogLevel level, string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        var state = Volatile.Read(ref _logCallbackState);
        if (state == null)
            return;

        var bytes = Encoding.UTF8.GetBytes(line);
        state.Invoke((int)level, bytes);
    }

    private abstract class CallbackStateBase
    {
        private int _inflight;
        private int _retired;

        protected bool TryEnter()
        {
            if (Volatile.Read(ref _retired) != 0)
                return false;

            Interlocked.Increment(ref _inflight);
            if (Volatile.Read(ref _retired) == 0)
                return true;

            Interlocked.Decrement(ref _inflight);
            return false;
        }

        protected void Exit()
        {
            Interlocked.Decrement(ref _inflight);
        }

        public void RetireAndWait()
        {
            Interlocked.Exchange(ref _retired, 1);
            var spinner = new SpinWait();
            while (Volatile.Read(ref _inflight) != 0)
                spinner.SpinOnce();
        }
    }

    private unsafe sealed class NativeLogCallbackState(
        delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback,
        IntPtr userData) : CallbackStateBase
    {
        public void Invoke(int level, byte[] bytes)
        {
            if (bytes.Length == 0 || !TryEnter())
                return;

            try
            {
                fixed (byte* ptr = bytes)
                {
                    callback(userData, level, ptr, bytes.Length);
                }
            }
            finally
            {
                Exit();
            }
        }
    }

    private unsafe sealed class NativeContainerStateCallbackState(
        delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> callback,
        IntPtr userData) : CallbackStateBase
    {
        public void Invoke(byte[] bytes)
        {
            if (bytes.Length == 0 || !TryEnter())
                return;

            try
            {
                fixed (byte* ptr = bytes)
                {
                    callback(userData, ptr, bytes.Length);
                }
            }
            finally
            {
                Exit();
            }
        }
    }
}

internal sealed class NativeContainer
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly string _logicalId;
    private readonly DateTimeOffset _createdAt;
    private string? _name;

    private PodishContainerSession? _session;
    private int? _exitCode;
    private bool _removed;
    private string _persistedState;

    private NativeOutputCallbackState? _outputCallbackState;

    public required NativeContext Owner { get; init; }
    public required PodishRunSpec Spec { get; init; }

    public NativeContainer()
    {
        _logicalId = Guid.NewGuid().ToString("N")[..12];
        _createdAt = DateTimeOffset.UtcNow;
        _persistedState = "created";
        _name = null;
    }

    public NativeContainer(string containerId, string? name, DateTimeOffset createdAt, string state, int? exitCode)
    {
        _logicalId = containerId;
        _name = string.IsNullOrWhiteSpace(name) ? null : name;
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
                return GetImageRefUnsafe();
            }
        }
    }

    public string? Name
    {
        get
        {
            lock (_gate)
            {
                return GetNameUnsafe();
            }
        }
    }

    public bool HasTerminal
    {
        get
        {
            lock (_gate)
            {
                return HasTerminalUnsafe();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return IsRunningUnsafe();
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
                return GetStateUnsafe();
            }
        }
    }

    public async Task StartAsync()
    {
        await _startGate.WaitAsync();
        try
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
        finally
        {
            _startGate.Release();
        }
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

    public bool ForceDestroy(int timeoutMs)
    {
        return Remove(force: true, timeoutMs);
    }

    public bool Remove(bool force, int timeoutMs)
    {
        _startGate.Wait();
        try
        {
            PodishContainerSession? session;
            lock (_gate)
            {
                if (_removed)
                    return true;

                session = _session;
                if (session != null && !session.IsCompleted && !force)
                    return false;

                _removed = true;
            }

            if (session != null && !session.IsCompleted)
                StopAndForceIfNeeded(session, timeoutMs);

            lock (_gate)
            {
                _persistedState = "exited";
                PersistMetadataLocked("exited");
            }

            DeleteMetadataDirectory();
            return true;
        }
        finally
        {
            _startGate.Release();
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
        var next = callback == null ? null : new NativeOutputCallbackState(callback, userData);
        var previous = Interlocked.Exchange(ref _outputCallbackState, next);

        lock (_gate)
            ApplyOutputCallbackLocked();

        previous?.RetireAndWait();
    }

    private void ApplyOutputCallbackLocked()
    {
        if (_session == null)
            return;

        if (Volatile.Read(ref _outputCallbackState) == null)
        {
            _session.SetOutputHandler(null);
            return;
        }

        _session.SetOutputHandler((kind, data) =>
        {
            var state = Volatile.Read(ref _outputCallbackState);
            if (state == null || data.Length == 0)
                return;

            state.Invoke(kind == Fiberish.Core.VFS.TTY.TtyEndpointKind.Stderr ? 2 : 1, data);
        });
    }

    public void InitializeMetadata()
    {
        lock (_gate)
        {
            _persistedState = "created";
            if (string.IsNullOrWhiteSpace(_name))
                _name = Spec.Name;
            PersistMetadataLocked("created");
        }
    }

    public void Rename(string? newName)
    {
        lock (_gate)
        {
            _name = string.IsNullOrWhiteSpace(newName) ? null : newName;
            PersistMetadataLocked(_persistedState);
        }
    }

    private void DeleteMetadataDirectory()
    {
        PodishContainerMetadataStore.Delete(Owner.Context.ContainersDir, _logicalId);
    }

    public PodishContainerMetadata BuildMetadataSnapshot()
    {
        lock (_gate)
        {
            return new PodishContainerMetadata
            {
                ContainerId = _logicalId,
                Name = GetNameUnsafe(),
                Image = GetImageRefUnsafe(),
                State = GetStateUnsafe(),
                HasTerminal = HasTerminalUnsafe(),
                Running = IsRunningUnsafe(),
                ExitCode = _exitCode,
                CreatedAt = _createdAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                Spec = Spec
            };
        }
    }

    private void PersistMetadataLocked(string forcedState)
    {
        var metadata = new PodishContainerMetadata
        {
            ContainerId = _logicalId,
            Name = GetNameUnsafe(),
            Image = GetImageRefUnsafe(),
            State = forcedState,
            HasTerminal = HasTerminalUnsafe(),
            Running = forcedState == "running",
            ExitCode = _exitCode,
            CreatedAt = _createdAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = Spec
        };
        PodishContainerMetadataStore.Write(Owner.Context.ContainersDir, metadata);
    }

    private string GetImageRefUnsafe()
    {
        return _session != null ? _session.ImageRef : (Spec.Image ?? Spec.Rootfs ?? string.Empty);
    }

    private string? GetNameUnsafe()
    {
        return !string.IsNullOrWhiteSpace(_name) ? _name : Spec.Name;
    }

    private bool HasTerminalUnsafe()
    {
        return _session?.HasTerminal ?? (Spec.Interactive && Spec.Tty);
    }

    private bool IsRunningUnsafe()
    {
        return _session != null && !_session.IsCompleted;
    }

    private string GetStateUnsafe()
    {
        if (_session == null)
            return _persistedState;
        return _session.IsCompleted ? "exited" : "running";
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

    private static void StopAndForceIfNeeded(PodishContainerSession session, int timeoutMs)
    {
        session.SignalInitProcess(15);
        if (timeoutMs <= 0)
        {
            session.ForceStop();
            return;
        }

        try
        {
            if (session.WaitAsync().Wait(TimeSpan.FromMilliseconds(timeoutMs)))
                return;

            session.ForceStop();
            session.WaitAsync().Wait(TimeSpan.FromMilliseconds(Math.Max(250, timeoutMs)));
        }
        catch
        {
            session.ForceStop();
        }
    }

    private abstract class CallbackStateBase
    {
        private int _inflight;
        private int _retired;

        protected bool TryEnter()
        {
            if (Volatile.Read(ref _retired) != 0)
                return false;

            Interlocked.Increment(ref _inflight);
            if (Volatile.Read(ref _retired) == 0)
                return true;

            Interlocked.Decrement(ref _inflight);
            return false;
        }

        protected void Exit()
        {
            Interlocked.Decrement(ref _inflight);
        }

        public void RetireAndWait()
        {
            Interlocked.Exchange(ref _retired, 1);
            var spinner = new SpinWait();
            while (Volatile.Read(ref _inflight) != 0)
                spinner.SpinOnce();
        }
    }

    private unsafe sealed class NativeOutputCallbackState(
        delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, void> callback,
        IntPtr userData) : CallbackStateBase
    {
        public void Invoke(int streamKind, byte[] data)
        {
            if (data.Length == 0 || !TryEnter())
                return;

            try
            {
                fixed (byte* ptr = data)
                {
                    callback(userData, streamKind, ptr, data.Length);
                }
            }
            finally
            {
                Exit();
            }
        }
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
    internal const int PodOk = 0;
    internal const int PodEinval = 22;
    internal const int PodEnoent = 2;
    internal const int PodEbusy = 16;
    internal const int PodEinternal = 10000;

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
            foreach (var container in nativeContext.ContainersSnapshot())
            {
                try
                {
                    container.ForceDestroy(1000);
                }
                catch
                {
                    // best-effort shutdown on context destroy
                }
                finally
                {
                    nativeContext.UnregisterContainer(container);
                }
            }

            nativeContext.SetLogCallback(null, IntPtr.Zero);
            nativeContext.SetContainerStateCallback(null, IntPtr.Zero);
            nativeContext.Dispose();
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
            var (container, createError, createCode) = ctx.CreateContainer(spec!);
            if (container == null)
                return SetErrorAndReturn(ctx, createError ?? "failed to create container", createCode);

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
            var metadata = container.BuildMetadataSnapshot();
            var inspect = new NativeContainerInspect(
                Handle: container.GetHashCode().ToString("x"),
                ContainerId: metadata.ContainerId,
                Name: metadata.Name ?? string.Empty,
                Image: metadata.Image,
                State: metadata.State,
                HasTerminal: metadata.HasTerminal,
                Running: metadata.Running,
                ExitCode: metadata.ExitCode,
                Spec: metadata.Spec);

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

    [UnmanagedCallersOnly(EntryPoint = "pod_container_rename", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodContainerRename(IntPtr containerHandle, IntPtr nameUtf8)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        var newName = PtrToString(nameUtf8);
        var owner = container.Owner;

        try
        {
            var (ok, renameError) = owner.TryRenameContainer(container, newName);
            if (!ok)
                return SetErrorAndReturn(owner, renameError ?? "container name already exists", PodEbusy);

            owner.EmitContainerStateChanged(BuildContainerListSnapshot(owner));
            return PodOk;
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(owner, ex.ToString(), PodEinternal);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_remove", CallConvs = [typeof(CallConvCdecl)])]
    public static int PodContainerRemove(IntPtr containerHandle, int force)
    {
        var container = FromContainerHandle(containerHandle);
        if (container == null)
            return PodEinval;

        try
        {
            if (!container.Remove(force != 0, 1000))
                return PodEbusy;

            container.Owner.UnregisterContainer(container);
            var snapshot = BuildContainerListSnapshot(container.Owner);
            container.Owner.EmitContainerStateChanged(snapshot);
        }
        catch (Exception ex)
        {
            return SetErrorAndReturn(container.Owner, ex.ToString(), PodEinternal);
        }

        return PodOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_container_close", CallConvs = [typeof(CallConvCdecl)])]
    public static void PodContainerClose(IntPtr containerHandle)
    {
        if (containerHandle == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(containerHandle);
        if (!handle.IsAllocated)
            return;

        handle.Free();
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
        {
            try
            {
                container.ForceDestroy(1000);
            }
            catch
            {
                // best-effort destroy
            }
            finally
            {
                container.Owner.UnregisterContainer(container);
            }
        }

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
        var list = PodishContainerMetadataStore.ReadAll(ctx.Context.ContainersDir)
            .Select(m => new NativeContainerListItem(
                Handle: string.Empty,
                ContainerId: m.ContainerId,
                Name: m.Name ?? string.Empty,
                Image: m.Image,
                State: string.Equals(m.State, "running", StringComparison.OrdinalIgnoreCase) ? "exited" : m.State,
                HasTerminal: m.HasTerminal,
                Running: false,
                ExitCode: m.ExitCode))
            .ToDictionary(x => x.ContainerId, x => x, StringComparer.Ordinal);

        foreach (var live in ctx.ContainersSnapshot())
        {
            var snapshot = live.BuildMetadataSnapshot();
            var item = new NativeContainerListItem(
                Handle: live.GetHashCode().ToString("x"),
                ContainerId: snapshot.ContainerId,
                Name: snapshot.Name ?? string.Empty,
                Image: snapshot.Image,
                State: snapshot.State,
                HasTerminal: snapshot.HasTerminal,
                Running: snapshot.Running,
                ExitCode: snapshot.ExitCode);
            list[live.ContainerId] = item;
        }

        return list.Values.OrderByDescending(x => x.Running).ThenBy(x => x.ContainerId).ToList();
    }

    private static NativeContainer? OpenContainerById(NativeContext ctx, string containerId)
    {
        return ctx.OpenContainerByIdOrName(containerId);
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

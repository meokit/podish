using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.Net;
using Fiberish.Diagnostics;
using Fiberish.Native;
using Microsoft.Extensions.Logging;
using Podish.Display;
using Podish.Core.Graphics;

namespace Podish.Core.Native;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct PodDesktopOptionsNative
{
    public byte* RootfsStoreDirUtf8;
    public byte* StateDirUtf8;
    public byte* AngleDirUtf8;
    public byte* GlesGuestDirUtf8;
    public byte* LogLevelUtf8;
    public nint MetalLayer;
    public int LogicalWidth;
    public int LogicalHeight;
    public int PixelWidth;
    public int PixelHeight;
    public uint Scale120;
    public int EnableGles;
    public int TouchMode;
    public delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, void> OnTextInputChanged;
    public delegate* unmanaged[Cdecl]<nint, int, void> OnCursorChanged;
    public delegate* unmanaged[Cdecl]<nint, int, byte*, void> OnStatusChanged;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, int, float, uint, int, byte*, int, int> RasterizeText;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, byte*, int, int> RasterizeIcon;
    public nint CallbackUserData;
    public int VmMemoryLimitMb;
    public int VmBlockCacheMb;
    public int GlobalClientDpi;
    public delegate* unmanaged[Cdecl]<nint, int, int, void> OnVmSettingsChanged;
}

internal sealed class NativeDesktopSession : IDisposable
{
    private const string DesktopContainerId = "desktop";

    private readonly string _rootfsStoreDir;
    private readonly string _stateDir;
    private readonly string _angleDir;
    private readonly string? _glesGuestDir;
    private readonly nint _metalLayer;
    private readonly int _initialLogicalWidth;
    private readonly int _initialLogicalHeight;
    private readonly int _initialPixelWidth;
    private readonly int _initialPixelHeight;
    private readonly uint _initialScale120;
    private readonly bool _enableGles;
    private readonly bool _touchMode;
    private readonly int _vmMemoryLimitMb;
    private readonly int _vmBlockCacheMb;
    private readonly int _initialGlobalDpi;
    private readonly DesktopHostCallbacks _callbacks;
    private readonly PodishContext _context;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private DesktopCompositorHost? _compositor;
    private DesktopVmSettingsState? _vmSettings;
    private ContainerRuntimeThread? _runtimeThread;
    private ContainerProcessController? _processController;
    private Task<int>? _runtimeTask;
    private string _lastError = string.Empty;
    private bool _started;
    private bool _disposed;

    public NativeDesktopSession(
        string rootfsStoreDir,
        string stateDir,
        string angleDir,
        string? glesGuestDir,
        string logLevel,
        nint metalLayer,
        int logicalWidth,
        int logicalHeight,
        int pixelWidth,
        int pixelHeight,
        uint scale120,
        bool enableGles,
        bool touchMode,
        int vmMemoryLimitMb,
        int vmBlockCacheMb,
        int globalClientDpi,
        DesktopHostCallbacks callbacks)
    {
        _rootfsStoreDir = rootfsStoreDir;
        _stateDir = stateDir;
        _angleDir = angleDir;
        _glesGuestDir = glesGuestDir;
        _metalLayer = metalLayer;
        _initialLogicalWidth = logicalWidth;
        _initialLogicalHeight = logicalHeight;
        _initialPixelWidth = pixelWidth;
        _initialPixelHeight = pixelHeight;
        _initialScale120 = scale120;
        _enableGles = enableGles;
        _touchMode = touchMode;
        _vmMemoryLimitMb = Math.Max(0, vmMemoryLimitMb);
        _vmBlockCacheMb = Math.Max(0, vmBlockCacheMb);
        _initialGlobalDpi = Math.Max(0, globalClientDpi);
        _callbacks = callbacks;

        Directory.CreateDirectory(stateDir);
        _context = new PodishContext(new PodishContextOptions
        {
            WorkDir = stateDir,
            LogLevel = string.IsNullOrWhiteSpace(logLevel) ? "info" : logLevel
        });
        _logger = _context.LoggerFactory.CreateLogger<NativeDesktopSession>();
    }

    public string LastError
    {
        get { lock (_gate) return _lastError; }
        set { lock (_gate) _lastError = value ?? string.Empty; }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeDesktopSession));
            if (_started) return;
            _started = true;
        }

        try
        {
#if PODISH_APPLE_VULKAN
            _callbacks.NotifyStatus(1, "Initializing DiligentCore Vulkan compositor...");
            const bool glesEnabled = false;
            bool hostZink = Environment.GetEnvironmentVariable("PODISH_GUEST_GL_DRIVER") == "virgl";
            var venus = VenusRenderer.TryLoad(Path.Combine(_angleDir, "PodishVenus.framework", "PodishVenus"), _logger,
                enableVirgl: hostZink)
                ?? throw new InvalidOperationException("Unable to start the bundled Venus Vulkan renderer.");
            if (hostZink && !venus.CanExportVirglTexture)
                throw new InvalidOperationException("Host Zink requires the bundled VirGL renderer and Metal texture export.");
#else
            _callbacks.NotifyStatus(1, "Initializing ANGLE Metal compositor...");
            var glesEnabled = _enableGles && !string.IsNullOrWhiteSpace(_glesGuestDir) &&
                              File.Exists(Path.Combine(_glesGuestDir, "libEGL.so.1"));
            VenusRenderer? venus = null;
            bool hostZink = false;
#endif

            _compositor = new DesktopCompositorHost(
                _context.LoggerFactory,
                _angleDir,
                _metalLayer,
                _initialLogicalWidth,
                _initialLogicalHeight,
                _initialPixelWidth,
                _initialPixelHeight,
                _initialScale120,
                glesEnabled,
                _touchMode,
                _callbacks);
            _compositor.StartRenderThread();

            _callbacks.NotifyStatus(2, "Booting Debian i386 desktop environment...");
            var containerDir = Path.Combine(_context.ContainersDir, DesktopContainerId);
            Directory.CreateDirectory(containerDir);
            var eventStore = new ContainerEventStore(Path.Combine(_context.FiberpodDir, "events.jsonl"));
            _processController = new ContainerProcessController();
            _runtimeThread = new ContainerRuntimeThread(_logger, _context.LoggerFactory);

            var guestEnvs = new List<string>
            {
                "LANG=C.UTF-8",
                "LC_ALL=C.UTF-8",
                "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/local/games:/usr/games",
                "TERM=xterm-256color",
                "QT_QPA_PLATFORM=wayland",
                "QT_QPA_PLATFORMTHEME=lxqt",
                "QT_WAYLAND_DISABLE_WINDOWDECORATION=0",
                "GDK_BACKEND=wayland",
                "SDL_VIDEODRIVER=wayland",
                "SDL_VIDEO_DRIVER=wayland",
                "XDG_SESSION_TYPE=wayland",
                "XDG_CURRENT_DESKTOP=LXQt",
                "XDG_SESSION_DESKTOP=lxqt",
                "XDG_CONFIG_DIRS=/etc:/etc/xdg:/usr/share",
                "XDG_DATA_DIRS=/usr/local/share:/usr/share",
                "XDG_MENU_PREFIX=lxqt-",
                $"PODISH_TOUCH_MODE={(_touchMode ? "1" : "0")}",
                "LIBGL_ALWAYS_SOFTWARE=0",
#if PODISH_APPLE_VULKAN
                "PODISH_VULKAN_PRESENT=explicit"
#else
                "LIBGL_ES=2",
                "LIBGL_GL=21",
                "LIBGL_EGL=/run/podish-gles/libEGL.so.1",
                "LIBGL_GLES=/run/podish-gles/libGLESv2.so.2"
#endif
            };
            if (hostZink)
                guestEnvs.Add("PODISH_GUEST_GL_DRIVER=virgl");
            var autostartApp = Environment.GetEnvironmentVariable("PODISH_AUTOSTART_APP");
            if (!string.IsNullOrWhiteSpace(autostartApp))
                guestEnvs.Add($"PODISH_AUTOSTART_APP={autostartApp}");

            _vmSettings = new DesktopVmSettingsState(_vmMemoryLimitMb, _vmBlockCacheMb, _callbacks);
            long? memoryQuotaBytes = _vmMemoryLimitMb > 0
                ? Math.Max(ContainerMemoryLimits.MinimumMemoryQuotaBytes, (long)_vmMemoryLimitMb * 1024L * 1024L)
                : null;
            string[] cpuOptions = _vmBlockCacheMb > 0
                ? [$"BlockCacheSize={(long)_vmBlockCacheMb * 1024L * 1024L}"]
                : [];

            var request = new ContainerRunRequest
            {
                RootfsPath = _rootfsStoreDir,
                ContainerName = DesktopContainerId,
                ContainerId = DesktopContainerId,
                Image = "podish/debian-lxqt:i386",
                Hostname = "podish",
                ContainerDir = containerDir,
                RootfsMode = false,
                UseOverlay = true,
                FileSystemBackend = ContainerFileSystemBackend.OverlaySilkfs,
                NetworkMode = NetworkMode.Host,
                User = "user",
                WorkingDir = "/home/user",
                Exe = "/usr/local/bin/podish-desktop-session",
                ExeArgs = [],
                GuestEnvs = [.. guestEnvs],
                MemoryQuotaBytes = memoryQuotaBytes,
                CpuOptions = cpuOptions,
                UseTty = false,
                EnableHostConsoleInput = false,
                UseEngineInit = true,
                LogDriver = ContainerLogDriver.JsonFile,
                EventStore = eventStore,
                EnableWaylandServer = true,
                WaylandDesktopWidth = _compositor.LogicalWidth,
                WaylandDesktopHeight = _compositor.LogicalHeight,
                EnableGles = glesEnabled,
                EnableKmsDrm = false,
                CompositorScanout = _compositor.Scanout,
                CompositorTextures = _compositor.GuestTextures,
                AngleLibraryDirectory = glesEnabled ? _angleDir : null,
                GlesGuestLibraryDirectory = glesEnabled ? _glesGuestDir : null,
                GuestVulkan = venus,
                // Mount a clean tmpfs at /run owned by desktop uid=1000 (0700) BEFORE ContainerGraphics.Mount
                // so /run/podish-gles is mounted inside /run instead of being shadowed by a later /run mount.
                MountRunTmpfs = true,
                RunTmpfsUid = 1000,
                RunTmpfsGid = 1000,
                RunTmpfsMode = 0x41C0,
                ProcessController = _processController,
                ConfigureVirtualDaemons = (runtime, scheduler, uts, parentPid) =>
                {
                    var registry = new VirtualDaemonRegistry(runtime.Syscalls, scheduler);
                    var waylandDaemon = new DesktopWaylandDaemon(
                        ContainerRunRequest.WaylandDisplaySocketPath,
                        parentPid,
                        _context.LoggerFactory,
                        _compositor.Presenter,
                        _compositor.IngressBridge,
                        _compositor.LogicalWidth,
                        _compositor.LogicalHeight,
                        enableGpuBuffers: _compositor.GuestTextures != null,
                        scale120: _compositor.Scale120,
                        mobileMode: _touchMode,
                        reservedBottomInset: _compositor.NavBarHeight,
                        initialGlobalDpi: _initialGlobalDpi,
                        onToplevelsChanged: _compositor.UpdateToplevelSnapshots,
                        enableExplicitSync: venus != null,
                        enableVenusPrime: venus != null,
                        enableVirglPrime: hostZink);
                    var proc = registry.Spawn(waylandDaemon, parentPid: parentPid, uts: uts);
                    var controlDaemon = new DesktopControlDaemon(
                        "/run/podish-control.sock",
                        parentPid,
                        _context.LoggerFactory,
                        _compositor.IngressBridge,
                        _vmSettings,
                        _touchMode);
                    registry.Spawn(controlDaemon, parentPid: parentPid, uts: uts);
                    _logger.LogInformation("Spawned embedded Wayland daemon pid={Pid} at {Path}",
                        proc.Process.TGID, waylandDaemon.UnixPath);
                }
            };

            _runtimeTask = _runtimeThread.Start(request);
            _ = ObserveExitAsync(_runtimeTask);
            _callbacks.NotifyStatus(3, "Desktop running");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Failed to start embedded Debian desktop");
            _callbacks.NotifyStatus(-1, ex.Message);
            throw;
        }
    }

    private async Task ObserveExitAsync(Task<int> task)
    {
        try
        {
            var exitCode = await task.ConfigureAwait(false);
            _logger.LogInformation("Embedded Debian desktop session exited with code {Code}", exitCode);
            _callbacks.NotifyStatus(4, $"Session exited ({exitCode})");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogError(ex, "Embedded Debian desktop session crashed");
            _callbacks.NotifyStatus(-1, ex.Message);
        }
    }

    public void Resize(int logicalW, int logicalH, int pixelW, int pixelH, uint scale120, int clientDpi = 0) =>
        _compositor?.RequestResize(logicalW, logicalH, pixelW, pixelH, scale120, clientDpi);

    public void SetGlobalDpi(int globalDpi) =>
        _compositor?.IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.SetGlobalDpi, ClientDpi: globalDpi));

    public void ReloadConfig() =>
        _compositor?.IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.ReloadConfig));

    public void SetVisible(bool visible) => _compositor?.SetVisible(visible);

    public void SetTrackpadMode(bool enabled) => _compositor?.SetTrackpadMode(enabled);

    public void TriggerNavAction(int action) => _compositor?.TriggerNavAction(action);

    public void PostInput(DisplayInputEvent ev)
    {
        if (_compositor != null && _compositor.TryInterceptInput(ev))
            return;
        _compositor?.IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.Input, ev));
    }

    public void FocusLauncher() => _compositor?.TriggerNavAction(1);

    public void Stop()
    {
        try
        {
            _compositor?.IngressBridge.Post(new DesktopIngressEvent(DesktopIngressKind.WindowClosed));
            _processController?.TrySignalInitProcess(15); // SIGTERM
            if (_runtimeTask != null && !_runtimeTask.Wait(TimeSpan.FromSeconds(2)))
            {
                _processController?.TrySignalInitProcess(9); // SIGKILL
                _processController?.TryForceStop();
                _runtimeTask.Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception while stopping desktop session");
        }

        _compositor?.Stop();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        _compositor?.Dispose();
        _compositor = null;
        _runtimeThread?.Dispose();
        _runtimeThread = null;
        _context.Dispose();
    }
}

public static unsafe class PodishDesktopApi
{
    private const int PodOk = 0;
    private const int PodErr = -1;
    private const int PodEinval = -22;

    private static string? ReadUtf8(byte* ptr) => ptr == null ? null : Marshal.PtrToStringUTF8((nint)ptr);

    private static int CopyUtf8(string text, byte* buffer, int capacity)
    {
        if (buffer == null || capacity <= 0) return 0;
        var bytes = Encoding.UTF8.GetBytes(text);
        var count = Math.Min(bytes.Length, capacity - 1);
        Marshal.Copy(bytes, 0, (nint)buffer, count);
        buffer[count] = 0;
        return count;
    }

    private static NativeDesktopSession? FromHandle(nint handle)
    {
        if (handle == 0) return null;
        var gc = GCHandle.FromIntPtr(handle);
        return gc.Target as NativeDesktopSession;
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int Create(PodDesktopOptionsNative* options, nint* outDesktop)
    {
        if (options == null || outDesktop == null) return PodEinval;
        *outDesktop = 0;
        try
        {
            var rootfsDir = ReadUtf8(options->RootfsStoreDirUtf8);
            var stateDir = ReadUtf8(options->StateDirUtf8);
            var angleDir = ReadUtf8(options->AngleDirUtf8);
            var glesGuestDir = ReadUtf8(options->GlesGuestDirUtf8);
            var logLevel = ReadUtf8(options->LogLevelUtf8) ?? "info";

            if (string.IsNullOrWhiteSpace(rootfsDir) || string.IsNullOrWhiteSpace(stateDir) ||
                string.IsNullOrWhiteSpace(angleDir) || options->MetalLayer == 0)
                return PodEinval;

            var callbacks = new DesktopHostCallbacks
            {
                OnTextInputChanged = options->OnTextInputChanged,
                OnCursorChanged = options->OnCursorChanged,
                OnStatusChanged = options->OnStatusChanged,
                RasterizeText = options->RasterizeText,
                RasterizeIcon = options->RasterizeIcon,
                UserData = options->CallbackUserData,
                OnVmSettingsChanged = options->OnVmSettingsChanged
            };

            var session = new NativeDesktopSession(
                rootfsDir, stateDir, angleDir, glesGuestDir, logLevel,
                options->MetalLayer,
                options->LogicalWidth > 0 ? options->LogicalWidth : 1024,
                options->LogicalHeight > 0 ? options->LogicalHeight : 768,
                options->PixelWidth > 0 ? options->PixelWidth : 1024,
                options->PixelHeight > 0 ? options->PixelHeight : 768,
                options->Scale120 > 0 ? options->Scale120 : 240u,
                options->EnableGles != 0,
                options->TouchMode != 0,
                options->VmMemoryLimitMb,
                options->VmBlockCacheMb,
                options->GlobalClientDpi,
                callbacks);

            *outDesktop = GCHandle.ToIntPtr(GCHandle.Alloc(session));
            return PodOk;
        }
        catch
        {
            return PodErr;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_start", CallConvs = [typeof(CallConvCdecl)])]
    public static int Start(nint desktop)
    {
        var session = FromHandle(desktop);
        if (session == null) return PodEinval;
        try
        {
            session.Start();
            return PodOk;
        }
        catch (Exception ex)
        {
            session.LastError = ex.Message;
            return PodErr;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_stop", CallConvs = [typeof(CallConvCdecl)])]
    public static void Stop(nint desktop)
    {
        FromHandle(desktop)?.Stop();
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void Destroy(nint desktop)
    {
        if (desktop == 0) return;
        var gc = GCHandle.FromIntPtr(desktop);
        try
        {
            if (gc.Target is NativeDesktopSession session)
                session.Dispose();
        }
        finally
        {
            if (gc.IsAllocated) gc.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_last_error", CallConvs = [typeof(CallConvCdecl)])]
    public static int LastError(nint desktop, byte* buffer, int capacity)
    {
        var session = FromHandle(desktop);
        return session == null ? 0 : CopyUtf8(session.LastError, buffer, capacity);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_resize", CallConvs = [typeof(CallConvCdecl)])]
    public static void Resize(nint desktop, int logicalW, int logicalH, int pixelW, int pixelH, uint scale120)
    {
        FromHandle(desktop)?.Resize(logicalW, logicalH, pixelW, pixelH, scale120);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_set_visible", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetVisible(nint desktop, int visible)
    {
        FromHandle(desktop)?.SetVisible(visible != 0);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_pointer_motion", CallConvs = [typeof(CallConvCdecl)])]
    public static void PointerMotion(nint desktop, int x, int y, uint timestampMs)
    {
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(DisplayInputEventKind.PointerMotion, x, y, timestampMs));
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_pointer_button", CallConvs = [typeof(CallConvCdecl)])]
    public static void PointerButton(nint desktop, int x, int y, uint button, int pressed, uint timestampMs)
    {
        var mapped = button switch
        {
            1 => DisplayPointerButton.Right,
            2 => DisplayPointerButton.Middle,
            0x111 => DisplayPointerButton.Right,
            0x112 => DisplayPointerButton.Middle,
            _ => DisplayPointerButton.Left
        };
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(
            DisplayInputEventKind.PointerButton, x, y, timestampMs, mapped, pressed != 0));
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_pointer_wheel", CallConvs = [typeof(CallConvCdecl)])]
    public static void PointerWheel(nint desktop, int dx, int dy, uint timestampMs)
    {
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(DisplayInputEventKind.PointerWheel, Timestamp: timestampMs)
        {
            DeltaX = dx,
            DeltaY = dy
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_pointer_leave", CallConvs = [typeof(CallConvCdecl)])]
    public static void PointerLeave(nint desktop, uint timestampMs)
    {
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(DisplayInputEventKind.PointerLeave, Timestamp: timestampMs));
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_key", CallConvs = [typeof(CallConvCdecl)])]
    public static void Key(nint desktop, uint evdevKey, int pressed, int handledByIme, uint timestampMs)
    {
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(
            DisplayInputEventKind.KeyboardKey, Timestamp: timestampMs, Pressed: pressed != 0, Key: evdevKey)
        {
            HandledByIme = handledByIme != 0
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_text_input", CallConvs = [typeof(CallConvCdecl)])]
    public static void TextInput(nint desktop, byte* textUtf8)
    {
        var text = ReadUtf8(textUtf8);
        if (string.IsNullOrEmpty(text)) return;
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(DisplayInputEventKind.TextInput, Text: text));
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_text_editing", CallConvs = [typeof(CallConvCdecl)])]
    public static void TextEditing(nint desktop, byte* preeditUtf8, int cursorBegin, int cursorEnd)
    {
        var text = ReadUtf8(preeditUtf8) ?? string.Empty;
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(DisplayInputEventKind.TextEditing,
            Text: text, CursorBegin: cursorBegin, CursorEnd: cursorEnd));
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_focus_changed", CallConvs = [typeof(CallConvCdecl)])]
    public static void FocusChanged(nint desktop, int focused)
    {
        FromHandle(desktop)?.PostInput(new DisplayInputEvent(
            focused != 0 ? DisplayInputEventKind.WindowFocusGained : DisplayInputEventKind.WindowFocusLost));
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_focus_launcher", CallConvs = [typeof(CallConvCdecl)])]
    public static void FocusLauncher(nint desktop)
    {
        FromHandle(desktop)?.FocusLauncher();
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_set_trackpad_mode", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetTrackpadMode(nint desktop, int enabled)
    {
        FromHandle(desktop)?.SetTrackpadMode(enabled != 0);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_set_client_scale", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetClientScale(nint desktop, int logicalW, int logicalH, int pixelW, int pixelH,
        uint scale120, int clientDpi)
    {
        FromHandle(desktop)?.Resize(logicalW, logicalH, pixelW, pixelH, scale120, clientDpi);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_set_global_dpi", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetGlobalDpi(nint desktop, int globalDpi)
    {
        FromHandle(desktop)?.SetGlobalDpi(globalDpi);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_reload_config", CallConvs = [typeof(CallConvCdecl)])]
    public static void ReloadConfig(nint desktop)
    {
        FromHandle(desktop)?.ReloadConfig();
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_nav_action", CallConvs = [typeof(CallConvCdecl)])]
    public static void NavAction(nint desktop, int action)
    {
        FromHandle(desktop)?.TriggerNavAction(action);
    }

    [UnmanagedCallersOnly(EntryPoint = "pod_desktop_reset_overlay", CallConvs = [typeof(CallConvCdecl)])]
    public static int ResetOverlay(byte* stateDirUtf8)
    {
        var stateDir = ReadUtf8(stateDirUtf8);
        if (string.IsNullOrWhiteSpace(stateDir)) return PodEinval;
        try
        {
            var upperDir = Path.Combine(stateDir, ".fiberpod", "containers", "desktop", "silk-upper");
            if (Directory.Exists(upperDir))
                Directory.Delete(upperDir, recursive: true);
            return PodOk;
        }
        catch
        {
            return PodErr;
        }
    }
}

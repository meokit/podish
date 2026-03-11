using System.Runtime.InteropServices;
using System.Threading;
using Fiberish.Native;

namespace Fiberish.Memory;

internal readonly record struct HostMemoryMapGeometry(
    int GuestPageSize,
    int HostPageSize,
    int AllocationGranularity,
    bool SupportsMappedFileBackend);

internal static partial class HostMemoryMapGeometryProvider
{
    private sealed class ScopeRestore : IDisposable
    {
        private readonly HostMemoryMapGeometry? _previous;

        public ScopeRestore(HostMemoryMapGeometry? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Override.Value = _previous;
        }
    }

    private static readonly AsyncLocal<HostMemoryMapGeometry?> Override = new();
    private static readonly Lazy<HostMemoryMapGeometry> Cached = new(CreateCurrent);

    public static HostMemoryMapGeometry GetCurrent()
    {
        return Override.Value ?? Cached.Value;
    }

    internal static IDisposable PushOverride(HostMemoryMapGeometry geometry)
    {
        var previous = Override.Value;
        Override.Value = geometry;
        return new ScopeRestore(previous);
    }

    private static HostMemoryMapGeometry CreateCurrent()
    {
        if (OperatingSystem.IsBrowser())
            return Unsupported();
#if NET8_0_OR_GREATER
        if (OperatingSystem.IsWasi())
            return Unsupported();
#endif

        var guestPageSize = LinuxConstants.PageSize;
        if (OperatingSystem.IsWindows())
        {
            GetSystemInfo(out var systemInfo);
            var hostPageSize = checked((int)systemInfo.dwPageSize);
            var granularity = checked((int)systemInfo.dwAllocationGranularity);
            return new HostMemoryMapGeometry(
                guestPageSize,
                hostPageSize > 0 ? hostPageSize : guestPageSize,
                granularity > 0 ? granularity : guestPageSize,
                SupportsMappedFileBackend: true);
        }

        var unixPageSize = Environment.SystemPageSize;
        if (unixPageSize <= 0)
            unixPageSize = guestPageSize;

        return new HostMemoryMapGeometry(
            guestPageSize,
            unixPageSize,
            unixPageSize,
            SupportsMappedFileBackend: true);
    }

    private static HostMemoryMapGeometry Unsupported()
    {
        return new HostMemoryMapGeometry(
            LinuxConstants.PageSize,
            LinuxConstants.PageSize,
            LinuxConstants.PageSize,
            SupportsMappedFileBackend: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_INFO
    {
        public ushort wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public nint lpMinimumApplicationAddress;
        public nint lpMaximumApplicationAddress;
        public nint dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);
}

using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Memory;

public readonly partial record struct HostMemoryMapGeometry(
    int GuestPageSize,
    int HostPageSize,
    int AllocationGranularity,
    bool SupportsMappedFileBackend,
    bool SupportsDirectMappedTailPage)
{
    public static HostMemoryMapGeometry CreateCurrent()
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
                true,
                false);
        }

        var unixPageSize = Environment.SystemPageSize;
        if (unixPageSize <= 0)
            unixPageSize = guestPageSize;

        return new HostMemoryMapGeometry(
            guestPageSize,
            unixPageSize,
            unixPageSize,
            true,
            true);
    }

    private static HostMemoryMapGeometry Unsupported()
    {
        return new HostMemoryMapGeometry(
            LinuxConstants.PageSize,
            LinuxConstants.PageSize,
            LinuxConstants.PageSize,
            false,
            false);
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

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
}

using System.Runtime.InteropServices;

namespace Fiberish.Syscalls;

[StructLayout(LayoutKind.Sequential)]
public struct Pollfd
{
    public int Fd;
    public short Events;
    public short Revents;
}

public static class PollEvents
{
    public const short POLLIN = 0x0001;
    public const short POLLPRI = 0x0002;
    public const short POLLOUT = 0x0004;
    public const short POLLERR = 0x0008;
    public const short POLLHUP = 0x0010;
    public const short POLLNVAL = 0x0020;
}

[StructLayout(LayoutKind.Sequential)]
public struct Timespec
{
    public int TvSec;
    public int TvNsec;
}

[StructLayout(LayoutKind.Sequential)]
public struct Timeval
{
    public int TvSec;
    public int TvUsec;
}

[StructLayout(LayoutKind.Sequential)]
public struct SysInfo
{
    public int Uptime;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public int[] Loads;

    public int TotalRam;
    public int FreeRam;
    public int SharedRam;
    public int BufferRam;
    public int TotalSwap;
    public int FreeSwap;
    public short Procs;
    public short Pad;
    public int TotalHigh;
    public int FreeHigh;
    public int MemUnit;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] // Padding to match Linux 32-bit layout
    public byte[] Padding;
}
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fiberish.Native;

namespace Fiberish.Core.VFS.TTY;

public static class MacOSTermios
{
    // macOS Termios Constants
    private const uint MAC_ICRNL = 0x100;
    private const uint MAC_INLCR = 0x40;
    private const uint MAC_IGNCR = 0x80;
    private const uint MAC_IXON = 0x200;
    private const uint MAC_IXOFF = 0x400;

    private const uint MAC_OPOST = 0x1;
    private const uint MAC_ONLCR = 0x2;

    public const uint MAC_CS8 = 0x300;
    public const uint MAC_CSIZE = 0x300;
    public const uint MAC_CREAD = 0x800;
    public const uint MAC_ICANON = 0x100;
    public const uint MAC_ECHO = 0x8;
    public const uint MAC_ECHOE = 0x10;
    public const uint MAC_ECHOK = 0x20;
    public const uint MAC_ECHONL = 0x40;
    public const uint MAC_ISIG = 0x80;
    public const uint MAC_IEXTEN = 0x400;

    private const int MAC_VMIN = 16;
    private const int MAC_VTIME = 17;
    private const int MAC_NCCS = 20;

    // Linux Termios Constants (i386/x86_64 generic)
    private const uint LINUX_ICANON = 0x2;
    private const uint LINUX_ECHO = 0x8;
    private const uint LINUX_ECHOE = 0x10;
    private const uint LINUX_ECHOK = 0x20;
    private const uint LINUX_ECHONL = 0x40;
    private const uint LINUX_ISIG = 0x1;
    private const uint LINUX_IEXTEN = 0x8000;

    private const uint LINUX_ICRNL = 0x100;
    private const uint LINUX_INLCR = 0x40;
    private const uint LINUX_IGNCR = 0x80;
    private const uint LINUX_IXON = 0x400;
    private const uint LINUX_IXOFF = 0x1000;

    private const uint LINUX_OPOST = 0x1;
    private const uint LINUX_ONLCR = 0x4;

    private const uint LINUX_CS8 = 0x30;
    private const uint LINUX_CREAD = 0x80;

    private const int LINUX_VMIN = 6;
    private const int LINUX_VTIME = 5;

    private static readonly object RawModeLock = new();
    private static readonly Dictionary<int, RawModeState> RawModeStates = [];

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, ref MacTermios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optional_actions, ref MacTermios termios);

    public static int EnableRawMode(int fd)
    {
        lock (RawModeLock)
        {
            if (RawModeStates.TryGetValue(fd, out var existing))
            {
                RawModeStates[fd] = existing with { RefCount = existing.RefCount + 1 };
                return 0;
            }

            MacTermios original = default;
            if (tcgetattr(fd, ref original) < 0) return -GetErrnoOrDefault();

            var raw = original;
            // Input: No break, no CR to NL, no parity check, no strip char
            // raw.c_iflag &= ~(ulong)(BRKINT | ICRNL | INPCK | ISTRIP | IXON);
            // We want strict raw, but let's just do what cfmakeraw usually does + tweaks
            raw.c_iflag &= ~(ulong)(MAC_ICRNL | MAC_IXON);
            raw.c_oflag &= ~(ulong)MAC_OPOST;
            raw.c_lflag &= ~(ulong)(MAC_ECHO | MAC_ICANON | MAC_IEXTEN | MAC_ISIG);

            // VMIN=1, VTIME=0 -> Blocking read until at least 1 byte
            raw.c_cc[MAC_VMIN] = 1;
            raw.c_cc[MAC_VTIME] = 0;

            if (tcsetattr(fd, 2 /* TCSAFLUSH */, ref raw) < 0) return -GetErrnoOrDefault();
            RawModeStates[fd] = new RawModeState(original, 1);
            return 0;
        }
    }

    public static void DisableRawMode(int fd)
    {
        lock (RawModeLock)
        {
            if (!RawModeStates.TryGetValue(fd, out var state)) return;

            if (state.RefCount > 1)
            {
                RawModeStates[fd] = state with { RefCount = state.RefCount - 1 };
                return;
            }

            var original = state.Original;
            tcsetattr(fd, 0 /* TCSANOW */, ref original);
            RawModeStates.Remove(fd);
        }
    }

    public static int GetAttr(int fd, byte[] linuxTermiosData)
    {
        if (linuxTermiosData.Length != LinuxConstants.TERMIOS_SIZE_I386) return -(int)Errno.EINVAL;

        var mac = new MacTermios { c_cc = new byte[MAC_NCCS] };
        if (tcgetattr(fd, ref mac) < 0) return -GetErrnoOrDefault();

        // Convert Mac -> Linux
        uint iflag = 0;
        if ((mac.c_iflag & MAC_ICRNL) != 0) iflag |= LINUX_ICRNL;
        if ((mac.c_iflag & MAC_INLCR) != 0) iflag |= LINUX_INLCR;
        if ((mac.c_iflag & MAC_IGNCR) != 0) iflag |= LINUX_IGNCR;
        if ((mac.c_iflag & MAC_IXON) != 0) iflag |= LINUX_IXON;
        if ((mac.c_iflag & MAC_IXOFF) != 0) iflag |= LINUX_IXOFF;

        uint oflag = 0;
        if ((mac.c_oflag & MAC_OPOST) != 0) oflag |= LINUX_OPOST;
        if ((mac.c_oflag & MAC_ONLCR) != 0) oflag |= LINUX_ONLCR;

        uint cflag = 0;
        if ((mac.c_cflag & MAC_CS8) != 0) cflag |= LINUX_CS8;
        if ((mac.c_cflag & MAC_CREAD) != 0) cflag |= LINUX_CREAD;

        uint lflag = 0;
        if ((mac.c_lflag & MAC_ICANON) != 0) lflag |= LINUX_ICANON;
        if ((mac.c_lflag & MAC_ECHO) != 0) lflag |= LINUX_ECHO;
        if ((mac.c_lflag & MAC_ECHOE) != 0) lflag |= LINUX_ECHOE;
        if ((mac.c_lflag & MAC_ECHOK) != 0) lflag |= LINUX_ECHOK;
        if ((mac.c_lflag & MAC_ECHONL) != 0) lflag |= LINUX_ECHONL;
        if ((mac.c_lflag & MAC_ISIG) != 0) lflag |= LINUX_ISIG;
        if ((mac.c_lflag & MAC_IEXTEN) != 0) lflag |= LINUX_IEXTEN;

        // Write to Linux Buffer
        var span = linuxTermiosData.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), iflag);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), oflag);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), cflag);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), lflag);
        span[16] = 0; // c_line

        // c_cc mapping
        for (var i = 0; i < 5; i++) span[17 + i] = mac.c_cc[i];
        span[17 + LINUX_VMIN] = mac.c_cc[MAC_VMIN];
        span[17 + LINUX_VTIME] = mac.c_cc[MAC_VTIME];

        return 0;
    }

    public static int SetAttr(int fd, int optional_actions, byte[] linuxTermiosData)
    {
        if (linuxTermiosData.Length != LinuxConstants.TERMIOS_SIZE_I386) return -(int)Errno.EINVAL;

        var mac = new MacTermios { c_cc = new byte[MAC_NCCS] };
        // Read current state first to preserve unmapped flags
        if (tcgetattr(fd, ref mac) < 0) return -GetErrnoOrDefault();

        var span = linuxTermiosData.AsSpan();
        var iflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
        var oflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        var cflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        var lflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));

        // Map Linux -> Mac
        // Input
        mac.c_iflag &= ~(ulong)(MAC_ICRNL | MAC_INLCR | MAC_IGNCR | MAC_IXON | MAC_IXOFF);
        if ((iflag & LINUX_ICRNL) != 0) mac.c_iflag |= MAC_ICRNL;
        if ((iflag & LINUX_INLCR) != 0) mac.c_iflag |= MAC_INLCR;
        if ((iflag & LINUX_IGNCR) != 0) mac.c_iflag |= MAC_IGNCR;
        if ((iflag & LINUX_IXON) != 0) mac.c_iflag |= MAC_IXON;
        if ((iflag & LINUX_IXOFF) != 0) mac.c_iflag |= MAC_IXOFF;

        // Output
        mac.c_oflag &= ~(ulong)(MAC_OPOST | MAC_ONLCR);
        if ((oflag & LINUX_OPOST) != 0) mac.c_oflag |= MAC_OPOST;
        if ((oflag & LINUX_ONLCR) != 0) mac.c_oflag |= MAC_ONLCR;

        // Control
        mac.c_cflag &= ~(ulong)MAC_CSIZE;
        if ((cflag & LINUX_CS8) != 0) mac.c_cflag |= MAC_CS8;
        if ((cflag & LINUX_CREAD) != 0) mac.c_cflag |= MAC_CREAD;

        // Local
        mac.c_lflag &= ~(ulong)(MAC_ICANON | MAC_ECHO | MAC_ECHOE | MAC_ECHOK | MAC_ECHONL | MAC_ISIG | MAC_IEXTEN);
        if ((lflag & LINUX_ICANON) != 0) mac.c_lflag |= MAC_ICANON;
        if ((lflag & LINUX_ECHO) != 0) mac.c_lflag |= MAC_ECHO;
        if ((lflag & LINUX_ECHOE) != 0) mac.c_lflag |= MAC_ECHOE;
        if ((lflag & LINUX_ECHOK) != 0) mac.c_lflag |= MAC_ECHOK;
        if ((lflag & LINUX_ECHONL) != 0) mac.c_lflag |= MAC_ECHONL;
        if ((lflag & LINUX_ISIG) != 0) mac.c_lflag |= MAC_ISIG;
        if ((lflag & LINUX_IEXTEN) != 0) mac.c_lflag |= MAC_IEXTEN;

        // c_cc 
        for (var i = 0; i < 5; i++) mac.c_cc[i] = span[17 + i];
        mac.c_cc[MAC_VMIN] = span[17 + LINUX_VMIN];
        mac.c_cc[MAC_VTIME] = span[17 + LINUX_VTIME];

        // Apply
        if (tcsetattr(fd, optional_actions, ref mac) < 0) return -GetErrnoOrDefault();

        return 0;
    }

    public static int GetWindowSize(int fd, out byte[] winSizeBytes)
    {
        try
        {
            // Use .NET Console API which safely handles the ioctl under the hood
            var cols = Console.WindowWidth;
            var rows = Console.WindowHeight;

            winSizeBytes = new byte[8];
            var span = winSizeBytes.AsSpan();
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(0, 2), (ushort)rows);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), (ushort)cols);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), 0); // xpixel
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), 0); // ypixel
            return 0;
        }
        catch
        {
            winSizeBytes = Array.Empty<byte>();
            // Return failure to trigger fallback
            return -1;
        }
    }

    private static int GetErrnoOrDefault()
    {
        var err = Marshal.GetLastPInvokeError();
        return err > 0 ? err : (int)Errno.EINVAL;
    }

    private readonly record struct RawModeState(MacTermios Original, int RefCount);

    [StructLayout(LayoutKind.Sequential)]
    public struct MacTermios
    {
        public ulong c_iflag; // 0
        public ulong c_oflag; // 8
        public ulong c_cflag; // 16
        public ulong c_lflag; // 24

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAC_NCCS)]
        public byte[] c_cc; // 32

        private uint _pad; // 52 (20 bytes of cc + this pad = 24 bytes, aligning to 8)
        public ulong c_ispeed; // 56
        public ulong c_ospeed; // 64
    }
}

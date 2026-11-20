using System.Buffers.Binary;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Native;
using Fiberish.Syscalls;

namespace Fiberish.VFS;

/// <summary>A single virtual terminal and PS/2 pointer attached to the framebuffer console.</summary>
public sealed class VirtualConsoleInput
{
    private volatile TtyDiscipline? _terminal;
    private Ps2MouseInode? _mouse;
    private EvdevInode? _evdevKeyboard, _evdevPointer;
    private int _keyboardMode = 1;
    private int _displayMode;
    private byte _leds;
    private readonly byte[] _vtMode = new byte[8];
    private readonly HashSet<uint> _heldKeys = [];
    private bool _capsLock;

    internal void Register(SuperBlock sb, TtyDiscipline? systemTty)
    {
        if (systemTty == null) return;
        var terminal = systemTty.CreateVirtualTerminal();
        foreach (var (name, rdev) in new[] { ("tty0", 0x0400), ("tty1", 0x0401), ("console", 0x0501) })
        {
            var inode = new VirtualTerminalInode(sb, terminal, this) { Rdev = (uint)rdev, Ino = (ulong)(0x20000 + rdev) };
            Add(sb.Root, name, inode);
        }

        var input = new Dentry(FsName.FromString("input"), null, sb.Root, sb);
        sb.Root.Inode!.Mkdir(input, 0x1ed, 0, 0);
        _mouse = new Ps2MouseInode(sb, terminal.Scheduler);
        Add(input, "mice", _mouse);
        var psaux = new Dentry(FsName.FromString("psaux"), null, sb.Root, sb);
        sb.Root.Inode.Symlink(psaux, "input/mice", 0, 0);
        _terminal = terminal;
    }

    internal void RegisterEvdev(SyscallManager syscalls, KernelScheduler scheduler)
    {
        syscalls.RegisterVirtualCharacterDevice("input/event0",
            sb => _evdevKeyboard = new EvdevInode(sb, scheduler, pointer: false));
        syscalls.RegisterVirtualCharacterDevice("input/event1",
            sb => _evdevPointer = new EvdevInode(sb, scheduler, pointer: true));
    }

    internal void Detach()
    {
        _terminal = null;
    }

    private static void Add(Dentry parent, string name, Inode inode)
    {
        var child = new Dentry(FsName.FromString(name), inode, parent, inode.SuperBlock);
        ((TmpfsInode)parent.Inode!).RegisterChild(parent, child.Name, child);
    }

    public void PublishPointer(int dx, int dy, int buttons)
    {
        var tty = _terminal;
        tty?.Scheduler.RunIngress(() =>
        {
            if (!ReferenceEquals(_terminal, tty)) return;
            _mouse!.Move(dx, dy, buttons);
            _evdevPointer?.PublishPointer(dx, dy, buttons);
        });
    }

    public void PublishKey(uint evdevKey, bool pressed)
    {
        var tty = _terminal;
        tty?.Scheduler.RunIngress(() =>
        {
            if (!ReferenceEquals(_terminal, tty)) return;
            Key(tty, evdevKey, pressed);
            _evdevKeyboard?.PublishKey(evdevKey, pressed);
        });
    }

    public void PublishWheel(int x, int y)
    {
        var tty = _terminal;
        tty?.Scheduler.RunIngress(() =>
        {
            if (ReferenceEquals(_terminal, tty)) _evdevPointer?.PublishWheel(x, y);
        });
    }

    private void Key(TtyDiscipline tty, uint code, bool pressed)
    {
        var changed = pressed ? _heldKeys.Add(code) : _heldKeys.Remove(code);
        if (code == 58 && pressed && changed) _capsLock = !_capsLock;
        var release = pressed ? 0 : 0x80;
        switch (_keyboardMode)
        {
            case 0: // K_RAW, PC/AT scan set 1
                if (code is > 0 and <= 88) tty.Input([(byte)((int)code | release)]);
                else if (ExtendedScanCode(code) is int scan) tty.Input([0xe0, (byte)(scan | release)]);
                break;
            case 2: // K_MEDIUMRAW uses Linux key codes with a release bit.
                if (code is > 0 and < 128) tty.Input([(byte)((int)code | release)]);
                else if (code < 16384) tty.Input([(byte)release, (byte)(0x80 | (code >> 7)), (byte)(0x80 | (code & 127))]);
                break;
            case 1: // K_XLATE
            case 3: // K_UNICODE (the built-in US keymap generates ASCII)
                if (!pressed) return;
                var shift = _heldKeys.Contains(42) || _heldKeys.Contains(54);
                var symbol = KeySymbol(code, shift ? 1 : 0);
                if ((symbol >> 8) == 11 && _capsLock) symbol ^= 32;
                if ((symbol >> 8) is 0 or 11)
                {
                    var ch = (byte)symbol;
                    if ((_heldKeys.Contains(29) || _heldKeys.Contains(97)) && char.IsAsciiLetter((char)ch))
                        ch &= 31;
                    tty.Input([ch]);
                }
                else if (code == 28) tty.Input([13]);
                break;
        }
    }

    private static int? ExtendedScanCode(uint code) => code switch
    {
        96 => 0x1c, 97 => 0x1d, 98 => 0x35, 99 => 0x37, 100 => 0x38,
        102 => 0x47, 103 => 0x48, 104 => 0x49, 105 => 0x4b, 106 => 0x4d,
        107 => 0x4f, 108 => 0x50, 109 => 0x51, 110 => 0x52, 111 => 0x53,
        125 => 0x5b, 126 => 0x5c, 127 => 0x5d, _ => null
    };

    // Linux console keysyms encode the type in the high byte (linux/keyboard.h).
    private static ushort KeySymbol(uint code, int table)
    {
        bool shift = (table & 1) != 0;
        int letter = code switch
        {
            >= 16 and <= 25 => "qwertyuiop"[(int)code - 16],
            >= 30 and <= 38 => "asdfghjkl"[(int)code - 30],
            >= 44 and <= 50 => "zxcvbnm"[(int)code - 44],
            _ => 0
        };
        if (letter != 0)
        {
            if ((table & 4) != 0) return (ushort)(letter & 31);
            return (ushort)(0xb00 | (shift ? char.ToUpperInvariant((char)letter) : letter));
        }
        return (ushort)(code switch
        {
            1 => 27, >= 2 and <= 13 => (shift ? "!@#$%^&*()_+" : "1234567890-=")[(int)code - 2],
            14 => 127, 15 => 9, 26 => shift ? '{' : '[', 27 => shift ? '}' : ']',
            28 => 0x201, 39 => shift ? ':' : ';', 40 => shift ? '"' : '\'',
            41 => shift ? '~' : '`', 43 => shift ? '|' : '\\', 51 => shift ? '<' : ',',
            52 => shift ? '>' : '.', 53 => shift ? '?' : '/', 57 => 32,
            29 or 97 => 0x702, 42 or 54 => 0x700, 56 => 0x703, 100 => 0x701,
            58 => 0x207, >= 59 and <= 68 => 0x100 + (int)code - 59, 87 or 88 => 0x10a + (int)code - 87,
            103 => 0x603, 105 => 0x601, 106 => 0x602, 108 => 0x600,
            102 => 0x114, 107 => 0x117, 104 => 0x118, 109 => 0x119, 110 => 0x115, 111 => 0x116,
            55 => 0x30c, 69 => 0x208, 70 => 0x209, 71 => 0x307, 72 => 0x308,
            73 => 0x309, 74 => 0x30b, 75 => 0x304, 76 => 0x305, 77 => 0x306,
            78 => 0x30a, 79 => 0x301, 80 => 0x302, 81 => 0x303, 82 => 0x300,
            83 => 0x310, 96 => 0x30e, 98 => 0x30d, _ => 0x200 // K_HOLE
        });
    }

    private sealed class VirtualTerminalInode(SuperBlock sb, TtyDiscipline tty, VirtualConsoleInput console)
        : ControllingTtyInode(sb)
    {
        public override void Open(LinuxFile file)
        {
            base.Open(file);
            file.PrivateData = tty;
        }

        public override int Ioctl(LinuxFile file, FiberTask task, uint request, uint arg)
        {
            var cpu = task.CPU;
            const int fault = -(int)Errno.EFAULT;
            const int invalid = -(int)Errno.EINVAL;
            switch (request)
            {
                case 0x5600: // VT_OPENQRY
                    return cpu.CopyToUser(arg, BitConverter.GetBytes(1)) ? 0 : fault;
                case 0x5601: // VT_GETMODE
                    return cpu.CopyToUser(arg, console._vtMode) ? 0 : fault;
                case 0x5602: // VT_SETMODE
                    Span<byte> mode = stackalloc byte[8];
                    if (!cpu.CopyFromUser(arg, mode)) return fault;
                    if (mode[0] > 1) return invalid;
                    mode.CopyTo(console._vtMode);
                    return 0;
                case 0x5603: // VT_GETSTATE
                    return cpu.CopyToUser(arg, [1, 0, 0, 0, 3, 0]) ? 0 : fault;
                case 0x5606: // VT_ACTIVATE
                case 0x5607: // VT_WAITACTIVE
                    return arg == 1 ? 0 : invalid;
                case 0x560c: // VT_LOCKSWITCH
                case 0x560d: // VT_UNLOCKSWITCH (only one terminal exists)
                    return 0;
                case 0x4b3a: // KDSETMODE
                    if (arg > 1) return invalid;
                    console._displayMode = (int)arg;
                    return 0;
                case 0x4b3b: // KDGETMODE
                    return cpu.CopyToUser(arg, BitConverter.GetBytes(console._displayMode)) ? 0 : fault;
                case 0x4b44: // KDGKBMODE
                    return cpu.CopyToUser(arg, BitConverter.GetBytes(console._keyboardMode)) ? 0 : fault;
                case 0x4b45: // KDSKBMODE
                    if (arg > 4) return invalid;
                    console._keyboardMode = (int)arg;
                    return 0;
                case 0x4b33: // KDGKBTYPE
                    return cpu.CopyToUser(arg, [2]) ? 0 : fault;
                case 0x4b31: // KDGETLED
                    return cpu.CopyToUser(arg, [console._leds]) ? 0 : fault;
                case 0x4b32: // KDSETLED
                    console._leds = (byte)(arg & 7);
                    return 0;
                case 0x4b46: // KDGKBENT
                    Span<byte> entry = stackalloc byte[4];
                    if (!cpu.CopyFromUser(arg, entry)) return fault;
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], KeySymbol(entry[1], entry[0]));
                    return cpu.CopyToUser(arg, entry) ? 0 : fault;
                default:
                    return base.Ioctl(file, task, request, arg);
            }
        }
    }
}

using System.Buffers.Binary;
using System.Text;
using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Core.VFS.TTY;

public class TtyDiscipline
{
    // Termios Special Characters Indices (Linux)
    private const int VINTR = 0;
    private const int VQUIT = 1;
    private const int VERASE = 2;
    private const int VKILL = 3;
    private const int VEOF = 4;
    private const int VTIME = 5;
    private const int VMIN = 6;
    private const int VSWTC = 7;
    private const int VSTART = 8;
    private const int VSTOP = 9;
    private const int VSUSP = 10;
    private const int VEOL = 11;
    private const int VREPRINT = 12;
    private const int VDISCARD = 13;
    private const int VWERASE = 14;
    private const int VLNEXT = 15;
    private const int VEOL2 = 16;

    // Input flags (iflag)
    private const uint IGNBRK = 0x01; // Ignore break condition
    private const uint BRKINT = 0x02; // Signal interrupt on break
    private const uint IGNPAR = 0x04; // Ignore characters with parity errors
    private const uint PARMRK = 0x08; // Mark parity errors
    private const uint INPCK = 0x10; // Enable input parity check
    private const uint ISTRIP = 0x20; // Strip 8th bit off characters
    private const uint INLCR = 0x40; // Map NL to CR on input
    private const uint IGNCR = 0x80; // Ignore CR
    private const uint ICRNL = 0x100; // Map CR to NL on input
    private const uint IUCLC = 0x200; // Map uppercase to lowercase
    private const uint IXON = 0x400; // Enable start/stop output control
    private const uint IXANY = 0x800; // Enable any character to restart output
    private const uint IXOFF = 0x1000; // Enable start/stop input control
    private const uint IMAXBEL = 0x2000; // Ring bell when input queue is full
    private const uint IUTF8 = 0x4000; // Input is UTF8

    // Output flags (oflag)
    private const uint OPOST = 1; // Enable output processing
    private const uint OLCUC = 2; // Map lowercase to uppercase
    private const uint ONLCR = 4; // Map NL to CR-NL
    private const uint OCRNL = 8; // Map CR to NL
    private const uint ONOCR = 16; // No CR output at column 0
    private const uint ONLRET = 32; // NL performs CR function
    private const uint OFILL = 64; // Use fill characters for delay
    private const uint OFDEL = 128; // Fill is DEL instead of NUL
    private const uint NLDLY = 0x100; // NL delay mask
    private const uint CRDLY = 0x600; // CR delay mask
    private const uint TABDLY = 0x1800; // Tab delay mask
    private const uint BSDLY = 0x2000; // BS delay mask
    private const uint VTDLY = 0x4000; // VT delay mask
    private const uint FFDLY = 0x8000; // FF delay mask

    // Local flags (lflag)
    private const uint ISIG = 1; // Enable signals
    private const uint ICANON = 2; // Canonical mode
    private const uint XCASE = 4; // Enable case mapping
    private const uint ECHO = 8; // Enable echo
    private const uint ECHOE = 16; // Echo erase character
    private const uint ECHOK = 32; // Echo kill character
    private const uint ECHONL = 64; // Echo NL even if ECHO is off
    private const uint NOFLSH = 128; // Disable flush after signal
    private const uint TOSTOP = 256; // Send SIGTTOU for background output
    private const uint ECHOCTL = 512; // Echo control characters as ^X
    private const uint ECHOPRT = 1024; // Echo erase character as character is erased
    private const uint ECHOKE = 2048; // Echo kill character by erasing line
    private const uint FLUSHO = 4096; // Output being flushed
    private const uint PENDIN = 8192; // Retype pending input
    private const uint IEXTEN = 16384; // Enable extended input character processing

    private const byte POSIX_VDISABLE = 0; // Or 255? Usually 0 in Linux for 'disabled' if not set
    private readonly ISignalBroadcaster _broadcaster;

    // Canonical mode buffer
    private readonly List<byte> _canonBuffer = new();
    private readonly byte[] _cc = new byte[32];
    private readonly ITtyDriver _driver;
    private readonly TtyInputQueue _inq = new();
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private uint _cflag = 0xbf; // CS8 | CREAD | ...
    private ushort _cols = 80;

    // Linux Termios fields
    private uint _iflag = 0x500; // ICRNL | IXON
    private uint _lflag = 0x8a3b | IEXTEN; // ISIG | ICANON | ECHO | ECHOE | ECHOK | IEXTEN

    // LNEXT state - next character should be treated literally
    private bool _lnextPending;
    private uint _oflag = 0x5; // OPOST | ONLCR

    // Flow control state
    private bool _outputStopped;

    // Window Size state
    private ushort _rows = 24;

    public TtyDiscipline(ITtyDriver driver, ISignalBroadcaster broadcaster, ILogger logger)
    {
        _driver = driver;
        _broadcaster = broadcaster;
        _logger = logger;
        Device = new TtyDevice();
        // Unify signaling: when data arrives in the device buffer (from background thread),
        // signal the input queue's wait handle so waiting readers are woken up.
        // This prevents the "lost wakeup" race condition where Read() might reset the event
        // just as new data arrives.
        Device.OnInputEnqueued += () => _inq.DataAvailable.Signal();
        InitializeDefaults();
    }

    // TTY hardware device for thread-safe input from background thread
    // InputLoop writes to this device, Read() processes it on scheduler thread
    public TtyDevice Device { get; }

    // Foreground process group
    public int ForegroundPgrp { get; set; }

    // Session ID that owns this TTY
    public int SessionId { get; set; }

    /// <summary>
    ///     Check if there is pending input in the device that hasn't been processed yet.
    ///     Used by scheduler to determine if it should wait for input.
    /// </summary>
    public bool HasPendingInput => Device.HasInterrupt;

    /// <summary>
    ///     Check if there is data available to read from the TTY.
    ///     This includes both pending input from the device and data already in the input queue.
    /// </summary>
    public bool HasDataAvailable => Device.HasInterrupt || _inq.Count > 0;

    public int BytesAvailable => _inq.Count;

    public AsyncWaitQueue DataAvailable => _inq.DataAvailable;

    private void InitializeDefaults()
    {
        // Defaults for Linux (like a standard terminal)
        _cc[VINTR] = 3; // ^C
        _cc[VQUIT] = 28; // ^\
        _cc[VERASE] = 127; // DEL
        _cc[VKILL] = 21; // ^U
        _cc[VEOF] = 4; // ^D
        _cc[VSTART] = 17; // ^Q
        _cc[VSTOP] = 19; // ^S
        _cc[VSUSP] = 26; // ^Z
        _cc[VREPRINT] = 18; // ^R
        _cc[VWERASE] = 23; // ^W
        _cc[VLNEXT] = 22; // ^V
        _cc[VMIN] = 1;
        _cc[VTIME] = 0;
        _cc[VEOL] = 0; // Disabled by default
        _cc[VEOL2] = 0; // Disabled by default
    }

    public int Read(Span<byte> buffer, FileFlags flags)
    {
        _logger.LogInformation("[TTY] Read: Called with buffer len={BufferLen}, flags={Flags}, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
            buffer.Length, flags, _inq.Count, _canonBuffer.Count);
        
        ProcessPendingInput();

        var result = _inq.Read(buffer, flags);
        
        _logger.LogInformation("[TTY] Read: _inq.Read returned {Result}, buffer contents=[{BufferContents}]",
            result, string.Join(", ", buffer.Slice(0, Math.Max(0, result)).ToArray().Select(b => $"0x{b:X2}")));

        // Race condition check: If new data arrived during processing/reading but after _inq.Read reset the event,
        // we must ensure the event is signaled again so we don't sleep forever in SysRead.
        // Only signal if there's actually data available to read (not just pending raw input).
        if (Device.HasInterrupt && _inq.Count > 0)
        {
            _logger.LogInformation("[TTY] Read: Race condition detected, re-signaling data available");
            _inq.DataAvailable.Signal();
        }

        return result;
    }

    public int Write(ReadOnlySpan<byte> buffer)
    {
        return OutputProcess(TtyEndpointKind.Stdout, buffer);
    }

    public int GetAttr(byte[] termiosData)
    {
        if (termiosData.Length != LinuxConstants.TERMIOS_SIZE_I386) return -(int)Errno.EINVAL;
        lock (_lock)
        {
            var span = termiosData.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), _iflag);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), _oflag);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), _cflag);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), _lflag);
            span[16] = 0; // c_line

            // cc
            // Linux struct typically has cc at offset 17
            for (var i = 0; i < 32 && i + 17 < termiosData.Length; i++) span[17 + i] = _cc[i];
        }

        return 0;
    }

    public int GetWindowSize(byte[] winSizeBytes)
    {
        if (winSizeBytes.Length != LinuxConstants.WINSIZE_SIZE) return -(int)Errno.EINVAL;

        // Return stored window size
        var span = winSizeBytes.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(0, 2), _rows); // rows
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), _cols); // cols
        return 0;
    }

    public int SetWindowSize(byte[] winSizeBytes)
    {
        if (winSizeBytes.Length != LinuxConstants.WINSIZE_SIZE) return -(int)Errno.EINVAL;

        var span = winSizeBytes.AsSpan();
        var rows = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
        var cols = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
        
        if (_rows != rows || _cols != cols)
        {
            _rows = rows;
            _cols = cols;
            // Send SIGWINCH to foreground process group
            _broadcaster.SignalProcessGroup(ForegroundPgrp, (int)Signal.SIGWINCH);
        }
        
        return 0;
    }

    public int SetAttr(int optional_actions, byte[] termiosData)
    {
        if (termiosData.Length != LinuxConstants.TERMIOS_SIZE_I386) return -(int)Errno.EINVAL;
        lock (_lock)
        {
            var span = termiosData.AsSpan();
            _iflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
            _oflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
            _cflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
            _lflag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));

            for (var i = 0; i < 32 && i + 17 < termiosData.Length; i++) _cc[i] = span[17 + i];

            // If switching from Canonical to Raw, or vice versa, we might need to flush or adjust buffers.
            // For now, if we switch OFF canonical mode, any pending canon buffer should probably be pushed to read queue?
            if ((_lflag & ICANON) == 0 && _canonBuffer.Count > 0)
            {
                // Flush canon buffer to read queue immediately
                _inq.Write(_canonBuffer.ToArray());
                _canonBuffer.Clear();
            }
        }

        return 0;
    }

    /// <summary>
    ///     Handle TTY ioctl operations. Returns negative errno on error, 0 on success.
    /// </summary>
    public int Ioctl(uint request, uint arg, Engine engine)
    {
        switch (request)
        {
            case LinuxConstants.TCGETS:
            {
                var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
                var ret = GetAttr(termios);
                if (ret != 0) return ret;
                if (!engine.CopyToUser(arg, termios)) return -(int)Errno.EFAULT;
                return 0;
            }
            case LinuxConstants.TCSETS:
            case LinuxConstants.TCSETSW:
            case LinuxConstants.TCSETSF:
            {
                var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
                if (!engine.CopyFromUser(arg, termios)) return -(int)Errno.EFAULT;
                return SetAttr((int)(request - LinuxConstants.TCGETS), termios);
            }
            case LinuxConstants.FIONREAD:
            {
                var count = BytesAvailable;
                var buf = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buf, count);
                if (!engine.CopyToUser(arg, buf)) return -(int)Errno.EFAULT;
                return 0;
            }
            case LinuxConstants.TIOCGWINSZ:
            {
                var buf = new byte[LinuxConstants.WINSIZE_SIZE];
                var ret = GetWindowSize(buf);
                if (ret == 0)
                    if (!engine.CopyToUser(arg, buf))
                        return -(int)Errno.EFAULT;
                return ret;
            }
            case LinuxConstants.TIOCSWINSZ:
            {
                var buf = new byte[LinuxConstants.WINSIZE_SIZE];
                if (!engine.CopyFromUser(arg, buf)) return -(int)Errno.EFAULT;
                return SetWindowSize(buf);
            }
            case LinuxConstants.TIOCGPGRP:
            {
                var pgid = ForegroundPgrp;
                if (!engine.CopyToUser(arg, BitConverter.GetBytes(pgid))) return -(int)Errno.EFAULT;
                return 0;
            }
            case LinuxConstants.TIOCSPGRP:
            {
                var buf = new byte[4];
                if (!engine.CopyFromUser(arg, buf)) return -(int)Errno.EFAULT;
                var pgid = BinaryPrimitives.ReadInt32LittleEndian(buf);
                if (pgid < 0) return -(int)Errno.EINVAL;

                var task = engine.Owner as FiberTask;
                if (task == null) return -(int)Errno.EPERM;

                // Verify pgid exists and belongs to the caller's session
                if (!KernelScheduler.Current!.IsValidProcessGroup(pgid, task.Process.SID))
                    return -(int)Errno.EPERM;

                ForegroundPgrp = pgid;
                return 0;
            }
            case LinuxConstants.TIOCSCTTY:
            {
                var task = engine.Owner as FiberTask;
                if (task == null) return -(int)Errno.EPERM;
                var proc = task.Process;

                if (proc.SID != proc.TGID) return -(int)Errno.EPERM; // Not session leader

                // Steal if arg=1
                if (SessionId != 0 && SessionId != proc.SID && arg != 1)
                    return -(int)Errno.EPERM;

                SessionId = proc.SID;
                ForegroundPgrp = proc.PGID;
                return 0;
            }
            default:
                return -(int)Errno.ENOTTY;
        }
    }

    /// <summary>
    ///     Called from background InputLoop thread to queue input data.
    ///     The data is stored in the TtyDevice buffer and will be processed
    ///     on the scheduler thread during Read() or ProcessPendingInput().
    ///     This ensures thread safety - complex TTY logic (echo, signals, canonical mode)
    ///     only runs on the scheduler thread.
    /// </summary>
    public void Input(byte[] input)
    {
        Device.EnqueueInput(input);
    }

    /// <summary>
    ///     Process pending input from the device. Called from scheduler thread.
    /// </summary>
    public void ProcessPendingInput()
    {
        _logger.LogInformation("[TTY] ProcessPendingInput: Device.HasInterrupt={HasInterrupt}, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
            Device.HasInterrupt, _inq.Count, _canonBuffer.Count);
        
        // Handle Resize first
        var resize = Device.ConsumeResize();
        if (resize.HasValue)
        {
            _logger.LogInformation("[TTY] ProcessPendingInput: Resize event detected {Rows}x{Cols}", resize.Value.Rows, resize.Value.Cols);
            HandleResize((ushort)resize.Value.Rows, (ushort)resize.Value.Cols);
        }

        // Handle Input Data
        var inputs = Device.ConsumeAll();
        if (inputs != null)
        {
            _logger.LogInformation("[TTY] ProcessPendingInput: Processing {Chunks} input chunks from device", inputs.Count);
            foreach (var inputData in inputs)
            {
                _logger.LogInformation("[TTY] ProcessPendingInput: Processing {Count} bytes: [{Data}]",
                    inputData.Length, string.Join(", ", inputData.Select(b => $"0x{b:X2}")));
                foreach (var b in inputData) InputByte(b);
            }

            _logger.LogInformation("[TTY] ProcessPendingInput: Done processing, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
                _inq.Count, _canonBuffer.Count);
        }
    }

    private void HandleResize(ushort rows, ushort cols)
    {
        if (_rows != rows || _cols != cols)
        {
            _logger.LogInformation("[TTY] HandleResize: Window size changing from {OldRows}x{OldCols} to {Rows}x{Cols}, sending SIGWINCH",
                _rows, _cols, rows, cols);
            _rows = rows;
            _cols = cols;
            SendSignal((int)Signal.SIGWINCH);
        }
    }

    private void InputByte(byte b)
    {
        _logger.LogInformation("[TTY] InputByte: {Char} (0x{Hex}), _canonBuffer.Count={CanonCount}, _inq.Count={InqCount}",
            (char)b, b.ToString("X2"), _canonBuffer.Count, _inq.Count);
        
        // Handle LNEXT (literal next) - this character should be treated literally
        if (_lnextPending)
        {
            _logger.LogInformation("[TTY] InputByte: LNEXT pending, treating 0x{Hex} literally", b.ToString("X2"));
            _lnextPending = false;
            ProcessRegularChar(b);
            return;
        }

        // Check for LNEXT character (^V) first - only in canonical mode with IEXTEN
        if ((_lflag & IEXTEN) != 0 && MatchCc(VLNEXT, b))
        {
            _lnextPending = true;
            // Echo ^V as ^V if ECHOCTL is set, otherwise just echo it
            if ((_lflag & ECHO) != 0)
            {
                if ((_lflag & ECHOCTL) != 0)
                    Echo(new[] { (byte)'^', (byte)'V' });
                else
                    Echo(new[] { b });
            }

            return;
        }

        // Handle software flow control (IXON/IXOFF)
        if ((_iflag & IXON) != 0)
        {
            // Check for VSTOP (^S) - stop output
            if (MatchCc(VSTOP, b))
            {
                _outputStopped = true;
                return;
            }

            // Check for VSTART (^Q) - restart output
            if (MatchCc(VSTART, b))
            {
                _outputStopped = false;
                return;
            }

            // If IXANY is set, any character restarts output
            if ((_iflag & IXANY) != 0 && _outputStopped)
                _outputStopped = false;
            // Continue processing this character
            else if (_outputStopped)
                // Output is stopped, discard this character
                return;
        }

        // Input processing (iflag)
        var processed = ProcessInputFlags(b);
        if (!processed.HasValue) return; // Character was consumed (e.g., IGNCR)
        b = processed.Value;

        // ISIG checks - signal generating characters
        if ((_lflag & ISIG) != 0)
        {
            if (MatchCc(VINTR, b)) // Ctrl-C
            {
                HandleSignalChar(2, "^C", b); // SIGINT
                return;
            }

            if (MatchCc(VQUIT, b)) // Ctrl-\
            {
                HandleSignalChar(3, "^\\", b); // SIGQUIT
                return;
            }

            if (MatchCc(VSUSP, b)) // Ctrl-Z
            {
                HandleSignalChar(20, "^Z", b); // SIGTSTP
                return;
            }
        }

        // Extended input processing (IEXTEN)
        if ((_lflag & IEXTEN) != 0)
        {
            // VREPRINT (^R) - reprint input line
            if (MatchCc(VREPRINT, b) && (_lflag & ICANON) != 0)
            {
                HandleReprint();
                return;
            }

            // VWERASE (^W) - word erase
            if (MatchCc(VWERASE, b) && (_lflag & ICANON) != 0)
            {
                CanonWordErase();
                return;
            }
        }

        // Regular character processing
        ProcessRegularChar(b);
    }

    private byte? ProcessInputFlags(byte b)
    {
        // Handle break condition (simplified - we don't have actual break detection)
        // In a real terminal, break is a special condition, not a regular byte

        // ISTRIP - strip 8th bit
        if ((_iflag & ISTRIP) != 0) b = (byte)(b & 0x7F);

        // IGNCR - ignore CR (must be checked before ICRNL)
        if ((_iflag & IGNCR) != 0 && b == 13) return null; // Return null to signal character was consumed

        // ICRNL - map CR to NL
        if ((_iflag & ICRNL) != 0 && b == 13)
            b = 10;
        // INLCR - map NL to CR (only if not already converted from CR)
        else if ((_iflag & INLCR) != 0 && b == 10) b = 13;

        return b;
    }

    private void HandleSignalChar(int signal, string echoStr, byte originalChar)
    {
        // Echo the signal character if ECHO is enabled
        if ((_lflag & ECHO) != 0)
        {
            if ((_lflag & ECHOCTL) != 0)
                Echo(Encoding.ASCII.GetBytes(echoStr + "\n"));
            else
                Echo(new[] { originalChar, (byte)'\n' });
        }

        // NOFLSH - if NOT set, flush input and output queues on signal
        if ((_lflag & NOFLSH) == 0)
        {
            _canonBuffer.Clear();
            _inq.Clear();
        }

        SendSignal(signal);
    }

    private void HandleReprint()
    {
        // Echo ^R followed by newline and the current line content
        if ((_lflag & ECHO) != 0)
        {
            if ((_lflag & ECHOCTL) != 0) Echo(new[] { (byte)'^', (byte)'R' });

            Echo(new[] { (byte)'\n' });

            // Reprint the current line
            foreach (var c in _canonBuffer) Echo(new[] { c });
        }
    }

    private void ProcessRegularChar(byte b)
    {
        _logger.LogInformation("[TTY] ProcessRegularChar: 0x{Hex} ({Char}), ICANON={IsCanon}, _canonBuffer.Count={CanonCount}",
            b.ToString("X2"), (char)b, (_lflag & ICANON) != 0, _canonBuffer.Count);
        
        // Canonical Mode
        if ((_lflag & ICANON) != 0)
        {
            if (b == 10) // EOL (NL)
            {
                _logger.LogInformation("[TTY] ProcessRegularChar: NL received, flushing canonical buffer");
                _canonBuffer.Add(b);
                // Echo NL - ECHO flag OR ECHONL flag
                if ((_lflag & ECHO) != 0 || (_lflag & ECHONL) != 0) EchoByte(TtyEndpointKind.Stdout, b);

                FlushCanonical(false);
            }
            else if (MatchCc(VEOF, b)) // EOF
            {
                _logger.LogInformation("[TTY] ProcessRegularChar: EOF received, flushing canonical buffer");
                // EOF character (Ctrl-D)
                FlushCanonical(true);
            }
            else if (MatchCc(VEOL, b) && _cc[VEOL] != POSIX_VDISABLE) // Alternate EOL
            {
                _logger.LogInformation("[TTY] ProcessRegularChar: VEOL received, flushing canonical buffer");
                _canonBuffer.Add(b);
                FlushCanonical(false);
            }
            else if (MatchCc(VEOL2, b) && _cc[VEOL2] != POSIX_VDISABLE) // Alternate EOL2
            {
                _logger.LogInformation("[TTY] ProcessRegularChar: VEOL2 received, flushing canonical buffer");
                _canonBuffer.Add(b);
                FlushCanonical(false);
            }
            else if (MatchCc(VERASE, b)) // Backspace
            {
                _logger.LogInformation("[TTY] ProcessRegularChar: VERASE received");
                CanonErase();
            }
            else if (MatchCc(VKILL, b)) // Kill Line
            {
                _logger.LogInformation("[TTY] ProcessRegularChar: VKILL received");
                CanonKill();
            }
            else
            {
                // Ordinary char
                // Buffer capacity check?
                if (_canonBuffer.Count < 4096)
                {
                    _canonBuffer.Add(b);
                    _logger.LogInformation("[TTY] ProcessRegularChar: Added 0x{Hex} to canon buffer, new count={Count}",
                        b.ToString("X2"), _canonBuffer.Count);
                    // Echo
                    if ((_lflag & ECHO) != 0) EchoByte(TtyEndpointKind.Stdout, b);
                }
            }
        }
        else
        {
            // Raw mode
            _logger.LogInformation("[TTY] ProcessRegularChar: Raw mode, writing 0x{Hex} directly to input queue", b.ToString("X2"));
            _inq.Write(b);
            if ((_lflag & ECHO) != 0) EchoByte(TtyEndpointKind.Stdout, b);
        }
    }

    private void Echo(byte[] bytes)
    {
        foreach (var b in bytes) EchoByte(TtyEndpointKind.Stdout, b);
    }

    private bool MatchCc(int cc, byte b)
    {
        return _cc[cc] != POSIX_VDISABLE && _cc[cc] == b;
    }

    private void FlushCanonical(bool eof)
    {
        _logger.LogInformation("[TTY] FlushCanonical: count={Count}, eof={Eof}, buffer contents=[{BufferContents}]",
            _canonBuffer.Count, eof, string.Join(", ", _canonBuffer.Select(b => $"0x{b:X2}")));
        
        // If empty and NOT eof, nothing to flush.
        // But if EOF, we must flush (even empty) to signal the reader (0 bytes read).
        if (_canonBuffer.Count == 0 && !eof)
        {
            _logger.LogInformation("[TTY] FlushCanonical: Buffer empty and not EOF, returning without flushing");
            return;
        }

        // Note: EOF itself is NOT pushed to queue in Linux canon mode,
        // it just terminates the read. If we have data, we push data.
        // If we have NO data and EOF, we push empty write to signal 0-read.

        var dataToWrite = _canonBuffer.ToArray();
        _logger.LogInformation("[TTY] FlushCanonical: Writing {Count} bytes to input queue: [{Data}]",
            dataToWrite.Length, string.Join(", ", dataToWrite.Select(b => $"0x{b:X2}")));
        _inq.Write(dataToWrite, true);
        _canonBuffer.Clear();

        if (eof)
        {
            _logger.LogInformation("[TTY] FlushCanonical: EOF was signaled, input queue marked as canonical ready");
            // If this was an EOF, we just flushed whatever was there.
            // If _canonBuffer was empty, _inq.Write with canonicalReady=true will ensure Read() returns.
        }
    }

    private void CanonErase()
    {
        if (_canonBuffer.Count == 0) return;

        // Handle multi-byte UTF-8 character erase
        var eraseCount = 1;
        if ((_lflag & IEXTEN) != 0 && _canonBuffer.Count > 0)
        {
            // Check if we're erasing a UTF-8 continuation byte
            // UTF-8 continuation bytes are 10xxxxxx (0x80-0xBF)
            // We need to find the start byte
            var idx = _canonBuffer.Count - 1;
            while (idx > 0 && (_canonBuffer[idx] & 0xC0) == 0x80)
            {
                idx--;
                eraseCount++;
            }

            // If we found a multi-byte sequence, erase all of it
            if (eraseCount > 1)
                _canonBuffer.RemoveRange(_canonBuffer.Count - eraseCount, eraseCount);
            else
                _canonBuffer.RemoveAt(_canonBuffer.Count - 1);
        }
        else
        {
            _canonBuffer.RemoveAt(_canonBuffer.Count - 1);
        }

        if ((_lflag & ECHO) != 0 && (_lflag & ECHOE) != 0)
            // Echo backspace-space-backspace for each erased character
            for (var i = 0; i < eraseCount; i++)
                OutputProcess(TtyEndpointKind.Stdout, new byte[] { 8, 32, 8 });
    }

    private void CanonWordErase()
    {
        if (_canonBuffer.Count == 0) return;

        var erasedCount = 0;

        // First, skip any trailing whitespace
        while (_canonBuffer.Count > 0 && IsWhitespace(_canonBuffer[_canonBuffer.Count - 1]))
        {
            _canonBuffer.RemoveAt(_canonBuffer.Count - 1);
            erasedCount++;
        }

        // Then erase the word (until we hit whitespace or beginning)
        while (_canonBuffer.Count > 0 && !IsWhitespace(_canonBuffer[_canonBuffer.Count - 1]))
        {
            _canonBuffer.RemoveAt(_canonBuffer.Count - 1);
            erasedCount++;
        }

        // Echo the erasure
        if ((_lflag & ECHO) != 0 && erasedCount > 0)
        {
            if ((_lflag & ECHOE) != 0)
            {
                // Echo backspace-space-backspace for each erased character
                for (var i = 0; i < erasedCount; i++) OutputProcess(TtyEndpointKind.Stdout, new byte[] { 8, 32, 8 });
            }
            else if ((_lflag & ECHOPRT) != 0)
            {
                // Echo the erased characters between \ and /
                Echo(new[] { (byte)'\\' });
                // Note: This would require tracking erased chars, simplified here
                Echo(new[] { (byte)'/' });
            }
        }
    }

    private static bool IsWhitespace(byte b)
    {
        return b == ' ' || b == '\t';
    }

    private void CanonKill()
    {
        if (_canonBuffer.Count == 0) return;

        var count = _canonBuffer.Count;
        _canonBuffer.Clear();

        if ((_lflag & ECHO) != 0)
        {
            if ((_lflag & ECHOK) != 0)
                // Echo newline
                OutputProcess(TtyEndpointKind.Stdout, new byte[] { 10 });
            else if ((_lflag & ECHOKE) != 0)
                // Echo backspace-space-backspace for each character
                for (var i = 0; i < count; i++)
                    OutputProcess(TtyEndpointKind.Stdout, new byte[] { 8, 32, 8 });
        }
    }

    private void EchoByte(TtyEndpointKind kind, byte b)
    {
        if ((_lflag & ECHO) == 0)
        {
            // Check ECHONL - echo NL even if ECHO is off
            if (b == 10 && (_lflag & ECHONL) != 0) OutputProcess(kind, new[] { b });

            return;
        }

        // ECHOCTL - echo control characters as ^X
        if ((_lflag & ECHOCTL) != 0 && b < 32 && b != 10 && b != 13 && b != 9)
        {
            OutputProcess(kind, new[] { (byte)'^', (byte)(b + 64) });
            return;
        }

        OutputProcess(kind, new[] { b });
    }

    private int OutputProcess(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
        // Check if output is stopped due to flow control
        if (_outputStopped)
            // In a real implementation, we would buffer this for later
            // For now, just discard (or could block)
            return buffer.Length;

        if ((_oflag & OPOST) == 0) return _driver.Write(kind, buffer);

        List<byte> expanded = new(buffer.Length + 16);

        foreach (var b in buffer)
            if (b == 10) // NL
            {
                // ONLCR - map NL to CR-NL
                if ((_oflag & ONLCR) != 0)
                {
                    expanded.Add(13);
                    expanded.Add(10);
                }
                // ONLRET - NL performs CR function (don't output CR, but move to col 0)
                else if ((_oflag & ONLRET) != 0)
                {
                    expanded.Add(10);
                }
                else
                {
                    expanded.Add(b);
                }
            }
            else if (b == 13) // CR
            {
                // OCRNL - map CR to NL
                if ((_oflag & OCRNL) != 0)
                    expanded.Add(10);
                // ONOCR - no CR at column 0 (we don't track column, so output anyway)
                else if ((_oflag & ONOCR) != 0)
                    // In a full implementation, we'd track column position
                    // For now, output the CR
                    expanded.Add(13);
                else
                    expanded.Add(13);
            }
            else if (b == 9) // TAB
            {
                // TABDLY - tab expansion (simplified)
                if ((_oflag & TABDLY) != 0)
                    // In a full implementation, expand to spaces based on column
                    // For now, just output the tab
                    expanded.Add(9);
                else
                    expanded.Add(9);
            }
            else
            {
                expanded.Add(b);
            }

        var expandedArray = expanded.ToArray();
        var written = _driver.Write(kind, expandedArray);
        if (written < 0) return written;

        // If partial write, mapping back is complex.
        // Assuming blocking write or full write for now.
        // If driver wrote everything, return original buffer length
        if (written == expandedArray.Length) return buffer.Length;

        return written; // Approximate
    }

    private void SendSignal(int sig)
    {
        _logger.LogInformation("[TTY] SendSignal: sig={Sig}, ForegroundPgrp={Pgrp}, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
            sig, ForegroundPgrp, _inq.Count, _canonBuffer.Count);
        
        if (ForegroundPgrp > 0)
        {
            _logger.LogInformation("[TTY] SendSignal: Signaling process group {Pgrp} with signal {Sig}", ForegroundPgrp, sig);
            _broadcaster.SignalProcessGroup(ForegroundPgrp, sig);
        }
        else
        {
            _logger.LogInformation("[TTY] SendSignal: Signaling foreground task with signal {Sig}", sig);
            _broadcaster.SignalForegroundTask(sig);
        }

        // Wake up read
        _logger.LogInformation("[TTY] SendSignal: Signaling input queue to wake up readers");
        _inq.Signal();
    }
}

internal sealed class TtyInputQueue
{
    private readonly object _lock = new();
    private readonly Queue<byte> _queue = new();
    private bool _hasCanonicalLine;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    public AsyncWaitQueue DataAvailable { get; } = new();

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _hasCanonicalLine = false;
        }
    }

    public void Write(byte b)
    {
        lock (_lock)
        {
            _queue.Enqueue(b);
            if (b == 10) _hasCanonicalLine = true;
        }

        DataAvailable.Set();
    }

    public void Write(IEnumerable<byte> bytes, bool canonicalReady = false)
    {
        lock (_lock)
        {
            foreach (var b in bytes)
            {
                _queue.Enqueue(b);
                if (b == 10) _hasCanonicalLine = true;
            }

            if (canonicalReady) _hasCanonicalLine = true;
        }

        DataAvailable.Set();
    }

    public void Signal()
    {
        // Wake up waiters to check signals
        DataAvailable.Set();
    }

    public int Read(Span<byte> buffer, FileFlags flags)
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                var count = 0;
                while (count < buffer.Length && _queue.Count > 0) buffer[count++] = _queue.Dequeue();

                // Only reset after consuming data, and only if queue is now empty
                if (_queue.Count == 0)
                {
                    _hasCanonicalLine = false;
                    DataAvailable.Reset();
                }

                return count;
            }

            // EOF in canonical mode: _hasCanonicalLine is set by FlushCanonical(true)
            // to signal that an EOF was received, even if the buffer is empty.
            if (_hasCanonicalLine && _queue.Count == 0)
            {
                _hasCanonicalLine = false;
                DataAvailable.Reset();
                return 0; // EOF
            }
        }

        // No data available.
        // DO NOT reset here - let WaitForRead() handle the reset after await.
        // This avoids race conditions where we reset just before new data arrives.
        // For non-blocking mode, return EAGAIN immediately.
        // For blocking mode, also return EAGAIN - the caller (SysRead) will handle
        // the blocking by awaiting WaitForRead() and then retrying.
        if ((flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;

        return -(int)Errno.EAGAIN;
    }
}
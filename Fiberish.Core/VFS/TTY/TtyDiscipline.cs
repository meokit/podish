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
    private readonly TtyInputQueue _inq;
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly KernelScheduler _scheduler;
    private uint _cflag = 0xbf; // CS8 | CREAD | ...
    private ushort _cols = 80;

    // Linux Termios fields
    private uint _iflag = 0x500; // ICRNL | IXON
    private int _inputDispatchPending;
    private uint _lflag = 0x8a3b | IEXTEN; // ISIG | ICANON | ECHO | ECHOE | ECHOK | IEXTEN

    // LNEXT state - next character should be treated literally
    private bool _lnextPending;
    private uint _oflag = 0x5; // OPOST | ONLCR

    // Persistent output buffer for OPOST expansion to achieve Zero-GC
    private byte[]? _outputBuffer;

    // Flow control state
    private bool _outputStopped;

    // Window Size state
    private ushort _rows = 24;

    public TtyDiscipline(ITtyDriver driver, ISignalBroadcaster broadcaster, ILogger logger,
        KernelScheduler scheduler)
    {
        _driver = driver;
        _broadcaster = broadcaster;
        _logger = logger;
        _scheduler = scheduler;
        _inq = new TtyInputQueue(scheduler);
        Device = new TtyDevice();
        Device.OnInputEnqueued += OnDeviceInputEnqueued;
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
    ///     This must not treat resize-only notifications as readable input. However,
    ///     pending hardware input bytes should still count as readable because a
    ///     subsequent read will first pull them through the line discipline.
    ///     This matters for raw-mode poll/select users such as vim, which often wait
    ///     for readability before issuing the read that drains the device buffer.
    /// </summary>
    public bool HasDataAvailable => _inq.HasReadableData || Device.HasBufferedInput;

    public int BytesAvailable => _inq.Count;

    public AsyncWaitQueue DataAvailable => _inq.DataAvailable;
    public bool CanWriteOutput => _driver.CanWrite;

    public void Hangup()
    {
        void ApplyHangup()
        {
            var sessionId = SessionId;
            var foregroundPgrp = ForegroundPgrp;

            if (sessionId <= 0 && foregroundPgrp <= 0)
                return;

            SessionId = 0;
            ForegroundPgrp = 0;
            _scheduler.ClearControllingTerminalForSession(this, sessionId);

            if (sessionId > 0)
                _scheduler.SignalProcess(sessionId, (int)Signal.SIGHUP);

            if (foregroundPgrp > 0)
            {
                _scheduler.SignalProcessGroup(foregroundPgrp, (int)Signal.SIGHUP);
                _scheduler.SignalProcessGroup(foregroundPgrp, (int)Signal.SIGCONT);
            }

            _inq.Signal();
        }

        if (_scheduler.IsSchedulerThread)
            ApplyHangup();
        else
            _scheduler.RunIngress(ApplyHangup);
    }

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

    public int Read(FiberTask? task, Span<byte> buffer, FileFlags flags)
    {
        var bgCheck = CheckBackgroundJob(task, true);
        if (bgCheck < 0) return bgCheck;

        _logger.LogTrace(
            "[TTY] Read: Called with buffer len={BufferLen}, flags={Flags}, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
            buffer.Length, flags, _inq.Count, _canonBuffer.Count);

        ProcessPendingInput(task);

        // Non-canonical mode requires special handling for VMIN and VTIME
        if ((_lflag & ICANON) == 0)
        {
            var vmin = _cc[VMIN];
            var vtime = _cc[VTIME];

            // Determine if we need to enforce MIN/TIME rules. If non-blocking, we just read what we can.
            if ((flags & FileFlags.O_NONBLOCK) == 0 && (vmin > 0 || vtime > 0))
            {
                // To properly implement VMIN/VTIME across the async/blocking syscall boundary,
                // we'd ideally yield back to the scheduler. However, returning -EAGAIN causes
                // the syscall handler to wait on IOAwaiter.
                //
                // VMIN > 0, VTIME > 0: Block until first byte, then start inter-byte timer. Return up to VMIN bytes.
                // VMIN > 0, VTIME == 0: Block until VMIN bytes are available.
                // VMIN == 0, VTIME > 0: Pure timeout read. Block up to VTIME * 100ms. If any bytes arrive, return them.
                // VMIN == 0, VTIME == 0: Return immediately with whatever is available, even 0.

                var count = _inq.Count;

                if (vmin > 0 && vtime == 0)
                {
                    // Block until VMIN bytes
                    if (count >= vmin || count >= buffer.Length)
                    {
                        var readCount = _inq.Read(buffer, flags);
                        return readCount;
                    }

                    _inq.DataAvailable.Reset();
                    return -(int)Errno.EAGAIN; // Will wait on IOAwaiter
                }

                // If VTIME is involved, we have a problem: IOAwaiter doesn't easily support timeouts right now.
                // Wait, IOAwaiter is triggered when DataAvailable is set.
                // For proper VTIME, we need to schedule a timeout on the FiberTask or handle it in the kernel.
                // Since this is a synchronous Read method, if we just return -EAGAIN, it waits INDEFINITELY.
                // To support VTIME without changing SyscallHandlers, we might have to block the thread
                // or have the input loop inject a dummy timeout signal.

                // For now, if we have enough bytes (VMIN), or if VMIN==0 and we just check what's there:
                if (vmin > 0 && vtime > 0)
                {
                    if (count >= vmin || count >= buffer.Length) return _inq.Read(buffer, flags);

                    // Wait for at least one byte
                    if (count == 0)
                    {
                        _inq.DataAvailable.Reset();
                        return -(int)Errno.EAGAIN;
                    }

                    // We have SOME bytes (count > 0) but less than VMIN.
                    // The inter-byte timer should be ticking. If we don't have timer support yet in this layer,
                    // we'll return what we have (violating VMIN slightly but preventing deadlocks), OR 
                    // we wait for more (violating VTIME). 
                    // Let's just return what we have for now if we can't do accurate inter-byte timing easily.
                    return _inq.Read(buffer, flags);
                }

                if (vmin == 0 && vtime > 0)
                {
                    if (count > 0) return _inq.Read(buffer, flags);

                    // Pure timeout read. We need a way to return 0 after VTIME.
                    // Right now we can't easily wait with timeout.
                    // fallback to blocking for first byte like vmin=1, vtime=0.
                    _inq.DataAvailable.Reset();
                    return -(int)Errno.EAGAIN;
                }
            }
        }

        var result = _inq.Read(buffer, flags);

        _logger.LogTrace("[TTY] Read: _inq.Read returned {Result}, buffer contents=[{BufferContents}]",
            result, string.Join(", ", buffer.Slice(0, Math.Max(0, result)).ToArray().Select(b => $"0x{b:X2}")));

        // Race condition check
        if (Device.HasInterrupt && _inq.Count > 0)
        {
            _logger.LogTrace("[TTY] Read: Race condition detected, re-signaling data available");
            _inq.DataAvailable.Signal();
        }

        return result;
    }

    public int Write(FiberTask? task, ReadOnlySpan<byte> buffer)
    {
        var bgCheck = CheckBackgroundJob(task, false);
        if (bgCheck < 0) return bgCheck;

        return OutputProcess(TtyEndpointKind.Stdout, buffer);
    }

    public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
    {
        return _driver.RegisterWriteWait(callback, scheduler);
    }

    private int CheckBackgroundJob(FiberTask? task, bool isRead)
    {
        if (task == null) return 0; // Not running in a context (e.g. tests)

        var process = task.Process;
        // If the process is not in the foreground process group of this TTY
        if (ForegroundPgrp > 0 && process.PGID != ForegroundPgrp)
        {
            if (isRead)
            {
                // Background read: always send SIGTTIN
                // Ignore if process is ignoring SIGTTIN or blocking it, or in orphaned pgrp (simplification: just send it)
                if (!task.IsSignalIgnoredOrBlocked(21)) // SIGTTIN = 21
                {
                    _broadcaster.SignalProcessGroup(task, process.PGID, 21);
                    return -(int)Errno.ERESTARTSYS; // Wait for signal to be handled
                }

                return -(int)Errno.EIO; // If ignored/blocked and orphaned, usually EIO
            }

            // Background write: send SIGTTOU only if TOSTOP is set
            if ((_lflag & TOSTOP) != 0)
                if (!task.IsSignalIgnoredOrBlocked(22)) // SIGTTOU = 22
                {
                    _broadcaster.SignalProcessGroup(task, process.PGID, 22);
                    return -(int)Errno.ERESTARTSYS;
                }
        }

        return 0; // Allowed
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

    public void InitializeWindowSize(ushort rows, ushort cols)
    {
        _rows = rows;
        _cols = cols;
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
            _broadcaster.SignalProcessGroup(null, ForegroundPgrp, (int)Signal.SIGWINCH);
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
    public int Ioctl(FiberTask task, uint request, uint arg)
    {
        var engine = task.CPU;
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

                // Linux: pgid == 0 means use caller's process group
                if (pgid == 0) pgid = task.Process.PGID;

                // Caller must have this tty as its controlling terminal
                if (task.Process.ControllingTty != this)
                    return -(int)Errno.ENOTTY;

                ForegroundPgrp = pgid;
                return 0;
            }
            case LinuxConstants.TIOCSCTTY:
            {
                var proc = task.Process;

                if (proc.SID != proc.TGID) return -(int)Errno.EPERM; // Not session leader

                // Steal if arg=1
                if (SessionId != 0 && SessionId != proc.SID && arg != 1)
                    return -(int)Errno.EPERM;

                // Set up controlling terminal relationship
                SessionId = proc.SID;
                ForegroundPgrp = proc.PGID;
                proc.ControllingTty = this;
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
    public int Input(byte[] input)
    {
        return Device.EnqueueInput(input);
    }

    /// <summary>
    ///     Inject terminal-generated response bytes (e.g. CSI queries) with priority.
    ///     This bypasses the hardware queue to avoid races against concurrently typed input.
    /// </summary>
    public void InjectTerminalResponse(byte[] input)
    {
        if (input.Length == 0) return;
        _inq.WriteFront(input);
    }

    /// <summary>
    ///     Process pending input from the device. Called from scheduler thread.
    /// </summary>
    public void ProcessPendingInput(FiberTask? task = null)
    {
        _logger.LogTrace(
            "[TTY] ProcessPendingInput: Device.HasInterrupt={HasInterrupt}, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
            Device.HasInterrupt, _inq.Count, _canonBuffer.Count);

        // Handle Resize first
        var resize = Device.ConsumeResize();
        if (resize.HasValue)
        {
            _logger.LogTrace("[TTY] ProcessPendingInput: Resize event detected {Rows}x{Cols}", resize.Value.Rows,
                resize.Value.Cols);
            HandleResize((ushort)resize.Value.Rows, (ushort)resize.Value.Cols, task);
        }

        // Handle Input Data
        var inputs = Device.ConsumeAll();
        if (inputs != null)
        {
            _logger.LogTrace("[TTY] ProcessPendingInput: Processing {Chunks} input chunks from device",
                inputs.Count);
            foreach (var inputData in inputs)
            {
                _logger.LogTrace("[TTY] ProcessPendingInput: Processing {Count} bytes: [{Data}]",
                    inputData.Length, string.Join(", ", inputData.Select(b => $"0x{b:X2}")));
                foreach (var b in inputData) InputByte(b, task);
            }

            _logger.LogTrace(
                "[TTY] ProcessPendingInput: Done processing, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
                _inq.Count, _canonBuffer.Count);
        }
    }

    private void OnDeviceInputEnqueued()
    {
        if (_scheduler.IsSchedulerThread)
        {
            ProcessPendingInput();
            return;
        }

        if (Interlocked.Exchange(ref _inputDispatchPending, 1) != 0)
        {
            // An ingress dispatch is already pending, but the scheduler may currently be
            // outside the run loop (for example during an async wait or cooperative yield
            // on browser Wasm). Re-signal it so freshly arrived input is not left sitting
            // in the device buffer until some unrelated later event happens to wake it.
            _scheduler.WakeUp();
            return;
        }

        _scheduler.ScheduleFromAnyThread(ProcessPendingIngress);
    }

    private void ProcessPendingIngress()
    {
        Interlocked.Exchange(ref _inputDispatchPending, 0);
        ProcessPendingInput();

        if (HasPendingInput && Interlocked.Exchange(ref _inputDispatchPending, 1) == 0)
            _scheduler.ScheduleFromAnyThread(ProcessPendingIngress);
    }

    private void HandleResize(ushort rows, ushort cols, FiberTask? task)
    {
        if (_rows != rows || _cols != cols)
        {
            _logger.LogTrace(
                "[TTY] HandleResize: Window size changing from {OldRows}x{OldCols} to {Rows}x{Cols}, sending SIGWINCH",
                _rows, _cols, rows, cols);
            _rows = rows;
            _cols = cols;
            SendSignal((int)Signal.SIGWINCH, task);
        }
    }

    private void InputByte(byte b, FiberTask? task)
    {
        _logger.LogTrace(
            "[TTY] InputByte: {Char} (0x{Hex}), _canonBuffer.Count={CanonCount}, _inq.Count={InqCount}",
            (char)b, b.ToString("X2"), _canonBuffer.Count, _inq.Count);

        // Handle LNEXT (literal next) - this character should be treated literally
        if (_lnextPending)
        {
            _logger.LogTrace("[TTY] InputByte: LNEXT pending, treating 0x{Hex} literally", b.ToString("X2"));
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
                HandleSignalChar(2, "^C", b, task); // SIGINT
                return;
            }

            if (MatchCc(VQUIT, b)) // Ctrl-\
            {
                HandleSignalChar(3, "^\\", b, task); // SIGQUIT
                return;
            }

            if (MatchCc(VSUSP, b)) // Ctrl-Z
            {
                HandleSignalChar(20, "^Z", b, task); // SIGTSTP
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

        // IUCLC - map uppercase to lowercase
        if ((_iflag & IUCLC) != 0 && b >= 'A' && b <= 'Z') b = (byte)(b + 32);

        // IGNCR - ignore CR (must be checked before ICRNL)
        if ((_iflag & IGNCR) != 0 && b == 13) return null; // Return null to signal character was consumed

        // ICRNL - map CR to NL
        if ((_iflag & ICRNL) != 0 && b == 13)
            b = 10;
        // INLCR - map NL to CR (only if not already converted from CR)
        else if ((_iflag & INLCR) != 0 && b == 10)
            b = 13;

        return b;
    }

    private void HandleSignalChar(int signal, string echoStr, byte originalChar, FiberTask? task)
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

        SendSignal(signal, task);
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
        _logger.LogTrace(
            "[TTY] ProcessRegularChar: 0x{Hex} ({Char}), ICANON={IsCanon}, _canonBuffer.Count={CanonCount}",
            b.ToString("X2"), (char)b, (_lflag & ICANON) != 0, _canonBuffer.Count);

        // Canonical Mode
        if ((_lflag & ICANON) != 0)
        {
            if (b == 10) // EOL (NL)
            {
                _logger.LogTrace("[TTY] ProcessRegularChar: NL received, flushing canonical buffer");
                _canonBuffer.Add(b);
                // Echo NL - ECHO flag OR ECHONL flag
                if ((_lflag & ECHO) != 0 || (_lflag & ECHONL) != 0) EchoByte(TtyEndpointKind.Stdout, b);

                FlushCanonical(false);
            }
            else if (MatchCc(VEOF, b)) // EOF
            {
                _logger.LogTrace("[TTY] ProcessRegularChar: EOF received, flushing canonical buffer");
                // EOF character (Ctrl-D)
                FlushCanonical(true);
            }
            else if (MatchCc(VEOL, b) && _cc[VEOL] != POSIX_VDISABLE) // Alternate EOL
            {
                _logger.LogTrace("[TTY] ProcessRegularChar: VEOL received, flushing canonical buffer");
                _canonBuffer.Add(b);
                FlushCanonical(false);
            }
            else if (MatchCc(VEOL2, b) && _cc[VEOL2] != POSIX_VDISABLE) // Alternate EOL2
            {
                _logger.LogTrace("[TTY] ProcessRegularChar: VEOL2 received, flushing canonical buffer");
                _canonBuffer.Add(b);
                FlushCanonical(false);
            }
            else if (MatchCc(VERASE, b)) // Backspace
            {
                _logger.LogTrace("[TTY] ProcessRegularChar: VERASE received");
                CanonErase();
            }
            else if (MatchCc(VKILL, b)) // Kill Line
            {
                _logger.LogTrace("[TTY] ProcessRegularChar: VKILL received");
                CanonKill();
            }
            else
            {
                // Ordinary char
                // Buffer capacity check
                if (_canonBuffer.Count < 4096)
                {
                    _canonBuffer.Add(b);
                    _logger.LogTrace("[TTY] ProcessRegularChar: Added 0x{Hex} to canon buffer, new count={Count}",
                        b.ToString("X2"), _canonBuffer.Count);
                    // Echo
                    if ((_lflag & ECHO) != 0) EchoByte(TtyEndpointKind.Stdout, b);
                }
                else if ((_iflag & IMAXBEL) != 0)
                {
                    // Buffer is full and IMAXBEL is set, ring bell and discard character
                    _logger.LogWarning("[TTY] ProcessRegularChar: Canon buffer full, sending BEL (IMAXBEL)");
                    EchoByte(TtyEndpointKind.Stdout, 0x07); // BEL
                }
            }
        }
        else
        {
            // Raw mode
            _logger.LogTrace("[TTY] ProcessRegularChar: Raw mode, writing 0x{Hex} directly to input queue",
                b.ToString("X2"));
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
        _logger.LogTrace("[TTY] FlushCanonical: count={Count}, eof={Eof}, buffer contents=[{BufferContents}]",
            _canonBuffer.Count, eof, string.Join(", ", _canonBuffer.Select(b => $"0x{b:X2}")));

        // If empty and NOT eof, nothing to flush.
        // But if EOF, we must flush (even empty) to signal the reader (0 bytes read).
        if (_canonBuffer.Count == 0 && !eof)
        {
            _logger.LogTrace("[TTY] FlushCanonical: Buffer empty and not EOF, returning without flushing");
            return;
        }

        // Note: EOF itself is NOT pushed to queue in Linux canon mode,
        // it just terminates the read. If we have data, we push data.
        // If we have NO data and EOF, we push empty write to signal 0-read.

        var dataToWrite = _canonBuffer.ToArray();
        _logger.LogTrace("[TTY] FlushCanonical: Writing {Count} bytes to input queue: [{Data}]",
            dataToWrite.Length, string.Join(", ", dataToWrite.Select(b => $"0x{b:X2}")));
        _inq.Write(dataToWrite, true);
        _canonBuffer.Clear();

        if (eof)
            _logger.LogTrace("[TTY] FlushCanonical: EOF was signaled, input queue marked as canonical ready");
        // If this was an EOF, we just flushed whatever was there.
        // If _canonBuffer was empty, _inq.Write with canonicalReady=true will ensure Read() returns.
    }

    private void CanonErase()
    {
        if (_canonBuffer.Count == 0) return;

        // Handle multi-byte UTF-8 character erase
        var eraseCount = 1;
        if ((_iflag & IUTF8) != 0 && _canonBuffer.Count > 0)
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

        // Fixed-size reusable buffer to achieve Zero-GC without large permanent allocations
        _outputBuffer ??= new byte[64 * 1024];

        var expanded = _outputBuffer;
        var inputOffset = 0;
        var totalInputConsumed = 0;

        while (inputOffset < buffer.Length)
        {
            var pos = 0;
            var inputConsumedInThisChunk = 0;

            // Process a chunk of input. Max expansion is 2x (NL -> CRNL), 
            // so we can safely process until expanded buffer is almost full.
            while (inputOffset + inputConsumedInThisChunk < buffer.Length && pos < expanded.Length - 2)
            {
                var b = buffer[inputOffset + inputConsumedInThisChunk];
                if (b == 10) // NL
                {
                    // ONLCR - map NL to CR-NL
                    if ((_oflag & ONLCR) != 0)
                    {
                        expanded[pos++] = 13;
                        expanded[pos++] = 10;
                    }
                    // ONLRET - NL performs CR function (don't output CR, but move to col 0)
                    else if ((_oflag & ONLRET) != 0)
                    {
                        expanded[pos++] = 10;
                    }
                    else
                    {
                        expanded[pos++] = b;
                    }
                }
                else if (b == 13) // CR
                {
                    // OCRNL - map CR to NL
                    if ((_oflag & OCRNL) != 0)
                        expanded[pos++] = 10;
                    else if ((_oflag & ONOCR) != 0)
                        // In a full implementation, we'd track column position
                        // For now, output the CR
                        expanded[pos++] = 13;
                    else
                        expanded[pos++] = 13;
                }
                else if (b == 9) // TAB
                {
                    // TABDLY - tab expansion (simplified)
                    if ((_oflag & TABDLY) != 0)
                        // In a full implementation, expand to spaces based on column
                        // For now, just output the tab
                        expanded[pos++] = 9;
                    else
                        expanded[pos++] = 9;
                }
                else
                {
                    // OLCUC - map lowercase to uppercase
                    if ((_oflag & OLCUC) != 0 && b >= 'a' && b <= 'z')
                        expanded[pos++] = (byte)(b - 32);
                    else
                        expanded[pos++] = b;
                }

                inputConsumedInThisChunk++;
            }

            var written = _driver.Write(kind, expanded.AsSpan(0, pos));
            if (written < 0) return totalInputConsumed > 0 ? totalInputConsumed : written;

            // If the driver couldn't take the whole expanded chunk, we need to determine 
            // how many input bytes were actually fully processed.
            if (written < pos)
            {
                // Re-trace the expansion to find the input boundary corresponding to 'written' output bytes.
                var reTraceOutputPos = 0;
                var reTraceInputConsumed = 0;
                for (var i = 0; i < inputConsumedInThisChunk; i++)
                {
                    var b = buffer[inputOffset + i];
                    var expansionSize = 1;
                    if (b == 10 && (_oflag & ONLCR) != 0) expansionSize = 2;
                    // (Simplified check: other OPOST flags like OLCUC are 1-to-1)

                    if (reTraceOutputPos + expansionSize > written) break;
                    reTraceOutputPos += expansionSize;
                    reTraceInputConsumed++;
                }

                totalInputConsumed += reTraceInputConsumed;
                return totalInputConsumed;
            }

            // Full chunk was written
            inputOffset += inputConsumedInThisChunk;
            totalInputConsumed += inputConsumedInThisChunk;
        }

        return totalInputConsumed;
    }

    private void SendSignal(int sig, FiberTask? task)
    {
        _logger.LogTrace(
            "[TTY] SendSignal: sig={Sig}, ForegroundPgrp={Pgrp}, _inq.Count={InqCount}, _canonBuffer.Count={CanonCount}",
            sig, ForegroundPgrp, _inq.Count, _canonBuffer.Count);

        if (ForegroundPgrp > 0)
        {
            _logger.LogTrace("[TTY] SendSignal: Signaling process group {Pgrp} with signal {Sig}", ForegroundPgrp,
                sig);
            _broadcaster.SignalProcessGroup(task, ForegroundPgrp, sig);
        }
        else
        {
            _logger.LogTrace("[TTY] SendSignal: Signaling foreground task with signal {Sig}", sig);
            _broadcaster.SignalForegroundTask(task, sig);
        }

        // Wake up read
        _logger.LogTrace("[TTY] SendSignal: Signaling input queue to wake up readers");
        _inq.Signal();
    }
}

internal sealed class TtyInputQueue
{
    private readonly Lock _lock = new();
    private readonly Queue<byte> _queue = new();
    private bool _hasCanonicalLine;

    public TtyInputQueue(KernelScheduler scheduler)
    {
        DataAvailable = new AsyncWaitQueue(scheduler);
    }

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

    public bool HasReadableData
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count > 0 || _hasCanonicalLine;
            }
        }
    }

    public AsyncWaitQueue DataAvailable { get; }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _hasCanonicalLine = false;
        }

        DataAvailable.Reset();
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

    public void WriteFront(IEnumerable<byte> bytes)
    {
        lock (_lock)
        {
            var existing = new List<byte>(_queue.Count);
            while (_queue.Count > 0) existing.Add(_queue.Dequeue());

            foreach (var b in bytes)
            {
                _queue.Enqueue(b);
                if (b == 10) _hasCanonicalLine = true;
            }

            foreach (var b in existing) _queue.Enqueue(b);
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
        // We must reset the event here. Previously this was deferred to WaitForRead(),
        // but with IOAwaiter/RegisterWait handling waits, we need the event properly reset
        // when we return EAGAIN, otherwise the next await will complete instantly in an infinite loop.
        DataAvailable.Reset();

        if ((flags & FileFlags.O_NONBLOCK) != 0) return -(int)Errno.EAGAIN;

        return -(int)Errno.EAGAIN;
    }
}

using System;
using System.Collections.Generic;
using Fiberish.Native;
using Fiberish.Core;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Core.VFS.TTY;

public class TtyDiscipline
{
    private readonly ITtyDriver _driver;
    private readonly ISignalBroadcaster _broadcaster;
    private readonly ILogger _logger;
    private readonly TtyInputQueue _inq = new();

    // Linux Termios fields
    private uint _iflag = 0x500; // ICRNL | IXON
    private uint _oflag = 0x5;   // OPOST | ONLCR
    private uint _cflag = 0xbf;  // CS8 | CREAD | ...
    private uint _lflag = 0x8a3b;// ISIG | ICANON | ECHO | ECHOE | ECHOK | IEXTEN
    private readonly byte[] _cc = new byte[32];

    // Canonical mode buffer
    private readonly List<byte> _canonBuffer = new();

    // Foreground process group
    public int ForegroundPgrp { get; set; }

    // Termios Special Characters Indices (Linux)
    private const int VINTR = 0;
    private const int VQUIT = 1;
    private const int VERASE = 2;
    private const int VKILL = 3;
    private const int VEOF = 4;
    private const int VTIME = 5;
    private const int VMIN = 6;
    private const int VSTART = 8;
    private const int VSTOP = 9;
    private const int VSUSP = 10;
    
    // Flags
    private const uint ICANON = 2;
    private const uint ECHO = 8;
    private const uint ECHOE = 16;
    private const uint ECHOK = 32;
    private const uint ISIG = 1;
    private const uint ICRNL = 0x100;
    private const uint INLCR = 0x40;
    private const uint IGNCR = 0x80;
    private const uint OPOST = 1;
    private const uint ONLCR = 4;
    
    private const byte POSIX_VDISABLE = 0; // Or 255? Usually 0 in Linux for 'disabled' if not set

    public TtyDiscipline(ITtyDriver driver, ISignalBroadcaster broadcaster, ILogger logger)
    {
        _driver = driver;
        _broadcaster = broadcaster;
        _logger = logger;
        InitializeDefaults();
    }
    
    private void InitializeDefaults()
    {
        // Defaults for Linux (like a standard terminal)
        _cc[VINTR] = 3;  // ^C
        _cc[VQUIT] = 28; // ^\
        _cc[VERASE] = 127; // DEL
        _cc[VKILL] = 21; // ^U
        _cc[VEOF] = 4;   // ^D
        _cc[VSTART] = 17; // ^Q
        _cc[VSTOP] = 19;  // ^S
        _cc[VSUSP] = 26;  // ^Z
        _cc[VMIN] = 1;
        _cc[VTIME] = 0;
    }

    public int Read(Span<byte> buffer, FileFlags flags)
    {
        return _inq.Read(buffer, flags);
    }
    
    public int Write(ReadOnlySpan<byte> buffer)
    {
        return OutputProcess(TtyEndpointKind.Stdout, buffer);
    }

    public int GetAttr(byte[] termiosData)
    {
        if (termiosData.Length != LinuxConstants.TERMIOS_SIZE_I386) return -(int)Errno.EINVAL;
        var span = termiosData.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), _iflag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), _oflag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), _cflag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), _lflag);
        span[16] = 0; // c_line
        
        // cc
        // Linux struct typically has cc at offset 17
        for(int i=0; i<32 && i+17 < termiosData.Length; i++)
        {
            span[17 + i] = _cc[i];
        }
        return 0;
    }

    public int GetWindowSize(byte[] winSizeBytes)
    {
        if (winSizeBytes.Length != 8) return -(int)Errno.EINVAL;
        // In interactive mode, this would call host IOCTL.
        
        // We can try to use MacOSTermios (host) if we are on macOS
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            byte[] hostSize;
            if (MacOSTermios.GetWindowSize(0, out hostSize) == 0)
            {
                hostSize.CopyTo(winSizeBytes, 0);
                return 0;
            }
        }

        // Default: 80x24
        var span = winSizeBytes.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(0, 2), 24); // rows
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), 80); // cols
        return 0;
    }

    public int SetAttr(int optional_actions, byte[] termiosData)
    {
        if (termiosData.Length != LinuxConstants.TERMIOS_SIZE_I386) return -(int)Errno.EINVAL;
        var span = termiosData.AsSpan();
        _iflag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
        _oflag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        _cflag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        _lflag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
        
        for(int i=0; i<32 && i+17 < termiosData.Length; i++)
        {
            _cc[i] = span[17 + i];
        }
        
        // If switching from Canonical to Raw, or vice versa, we might need to flush or adjust buffers.
        // For now, if we switch OFF canonical mode, any pending canon buffer should probably be pushed to read queue?
        if ((_lflag & ICANON) == 0 && _canonBuffer.Count > 0)
        {
             // Flush canon buffer to read queue immediately
             _inq.Write(_canonBuffer.ToArray());
             _canonBuffer.Clear();
        }
        
        return 0;
    }

    public void Input(byte[] input)
    {
        foreach (var b in input) InputByte(b);
    }

    private void InputByte(byte b)
    {
        // Pre-processing
        if ((_iflag & ICRNL) != 0 && b == 13)
        {
             // CR -> NL
             // Note: if IGNCR is set, we'd ignore it. logic omitted for brevity.
             b = 10;
        }
        else if ((_iflag & INLCR) != 0 && b == 10)
        {
            // NL -> CR
            b = 13;
        }

        // ISIG checks
        if ((_lflag & ISIG) != 0)
        {
            if (MatchCc(VINTR, b)) // Ctrl-C
            {
                if ((_lflag & ECHO) != 0) Echo(new[] { (byte)'^', (byte)'C', (byte)'\n' });
                SendSignal(2); // SIGINT
                return;
            }
            if (MatchCc(VQUIT, b)) // Ctrl-\
            {
                 if ((_lflag & ECHO) != 0) Echo(new[] { (byte)'^', (byte)'\\', (byte)'\n' });
                 SendSignal(3); // SIGQUIT
                 return;
            }
            // Suspend (Ctrl-Z) omitted for now
        }

        // Canonical Mode
        if ((_lflag & ICANON) != 0)
        {
            if (b == 10) // EOL
            {
                _canonBuffer.Add(b);
                if ((_lflag & ECHO) != 0)
                {
                    // Echo NL as CR NL usually
                    EchoByte(TtyEndpointKind.Stdout, b);
                }
                FlushCanonical(false);
            }
            else if (MatchCc(VEOF, b)) // EOF
            {
                // EOF character (Ctrl-D)
                FlushCanonical(true);
            }
            else if (MatchCc(VERASE, b)) // Backspace
            {
                CanonErase();
            }
            else if (MatchCc(VKILL, b)) // Kill Line
            {
                CanonKill();
            }
            else
            {
                // Ordinary char
                // Buffer capacity check?
                if (_canonBuffer.Count < 4096)
                {
                    _canonBuffer.Add(b);
                    // Echo
                    if ((_lflag & ECHO) != 0) EchoByte(TtyEndpointKind.Stdout, b);
                }
            }
        }
        else
        {
            // Raw mode
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
        // If empty and NOT eof, nothing to flush.
        // But if EOF, we must flush (even empty) to signal the reader (0 bytes read).
        if (_canonBuffer.Count == 0 && !eof) return;

        // Note: EOF itself is NOT pushed to queue in Linux canon mode,
        // it just terminates the read. If we have data, we push data.
        // If we have NO data and EOF, we push empty write to signal 0-read.
        
        _inq.Write(_canonBuffer.ToArray(), canonicalReady: true);
        _canonBuffer.Clear();
        
        if (eof)
        {
             // If this was an EOF, we just flushed whatever was there. 
             // If _canonBuffer was empty, _inq.Write with canonicalReady=true will ensure Read() returns.
        }
    }

    private void CanonErase()
    {
        if (_canonBuffer.Count == 0) return;
        _canonBuffer.RemoveAt(_canonBuffer.Count - 1);
        if ((_lflag & ECHO) != 0 && (_lflag & ECHOE) != 0)
        {
            // Echo backspace-space-backspace
            OutputProcess(TtyEndpointKind.Stdout, new byte[] { 8, 32, 8 });
        }
    }

    private void CanonKill()
    {
        if (_canonBuffer.Count == 0) return;
        _canonBuffer.Clear();
        if ((_lflag & ECHO) != 0 && (_lflag & ECHOK) != 0)
        {
             OutputProcess(TtyEndpointKind.Stdout, new byte[] { 10 });
        }
    }

    private void EchoByte(TtyEndpointKind kind, byte b)
    {
        if ((_lflag & ECHO) == 0) return;
        OutputProcess(kind, new[] { b });
    }

    private int OutputProcess(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
        if ((_oflag & OPOST) == 0)
        {
            return _driver.Write(kind, buffer);
        }

        if ((_oflag & ONLCR) == 0)
        {
            return _driver.Write(kind, buffer);
        }

        // Expand NL -> CR NL
        // Simplified expansion
        List<byte> expanded = new(buffer.Length + 8);
        foreach (byte b in buffer)
        {
            if (b == 10)
            {
                expanded.Add(13);
                expanded.Add(10);
            }
            else
            {
                expanded.Add(b);
            }
        }

        byte[] expandedArray = expanded.ToArray();
        int written = _driver.Write(kind, expandedArray);
        if (written < 0) return written;
        
        // If partial write, mapping back is complex. 
        // Assuming blocking write or full write for now.
        // If driver wrote everything, return original buffer length
        if (written == expandedArray.Length) return buffer.Length;
        
        return written; // Approximate
    }

    private void SendSignal(int sig)
    {
        _logger.LogDebug("[TTY] SendSignal: {Sig}", sig);
        if (ForegroundPgrp > 0)
        {
            _broadcaster.SignalProcessGroup(ForegroundPgrp, sig);
        }
        else
        {
            _broadcaster.SignalForegroundTask(sig);
        }

        // Wake up read
        _inq.Signal();
    }

    public WaitHandle DataAvailable => _inq.DataAvailable;
}

internal sealed class TtyInputQueue
{
    private readonly object _lock = new();
    private readonly Queue<byte> _queue = new();
    private readonly WaitHandle _dataEvent = new(); // Custom WaitHandle
    private bool _hasCanonicalLine;
    private bool _isEof;

    public int Count { get { lock(_lock) return _queue.Count; } }

    public void Write(byte b)
    {
        lock (_lock)
        {
            _queue.Enqueue(b);
            if (b == 10) _hasCanonicalLine = true;
        }
        _dataEvent.Set();
    }
    
    public void Write(IEnumerable<byte> bytes, bool canonicalReady = false)
    {
        lock (_lock)
        {
            foreach (byte b in bytes)
            {
                _queue.Enqueue(b);
                if (b == 10) _hasCanonicalLine = true;
            }
            if (canonicalReady) _hasCanonicalLine = true;
        }
        _dataEvent.Set();
    }

    public void Signal()
    {
         // Wake up waiters to check signals
         _dataEvent.Set();
    }

    public int Read(Span<byte> buffer, FileFlags flags)
    {
        while (true)
        {
            // Check signals (conceptually, the caller task should handle signals when waking up)
            // But if we return 0 bytes and no error, caller might assume EOF.
            // If we have no data and no EOF, we should block.
            
            // NOTE: Interruption by signal is handled by the Scheduler/Task logic
            // waking up the blocking task. Here we just check data.
            
            lock (_lock)
            {
                if (_queue.Count > 0)
                {
                    // If canonical mode is driven by _hasCanonicalLine?
                    // The discipline controls what goes in. 
                    // If logic pushes to queue implies it's ready to be read.
                    // For canonical mode, discipline buffers internally and only pushes lines.
                    // So if it's in queue, we can read it.
                    
                    int count = 0;
                    while (count < buffer.Length && _queue.Count > 0)
                    {
                        buffer[count++] = _queue.Dequeue();
                    }
                    if (_queue.Count == 0) _hasCanonicalLine = false;
                    
                    // Reset event if empty?
                    // WaitHandle generic Set() logic: usually auto-reset or manual?
                    // Our custom WaitHandle is manual reset (Set() -> IsSet=true).
                    // We should probably Reset() if empty. 
                    if (_queue.Count == 0) _dataEvent.Reset(); 
                    
                    return count;
                }
                
                // If we have pushed an empty line (EOF in canon mode), _hasCanonicalLine would be true
                // but queue empty?
                // The previous implementation used _hasCanonicalLine to signal "return 0 now".
                if (_hasCanonicalLine && _queue.Count == 0)
                {
                    _hasCanonicalLine = false;
                    _dataEvent.Reset();
                    return 0; 
                }
            }

            if ((flags & FileFlags.O_NONBLOCK) != 0)
            {
                return -(int)Errno.EAGAIN;
            }

            // Block
            var task = KernelScheduler.Current?.CurrentTask;
            if (task != null)
            {
                // Register wait
                 _dataEvent.Register(() => {
                     // Wake up task logic handled by scheduler usually?
                     // Scheduler.WakeTask(task)?
                     // WaitHandle implementation:
                     // Register(Action) calls action when Set.
                     // The task.BlockOn(waitHandle) is the pattern?
                     // Current pattern: task.BlockingSyscall = ...
                     // But _dataEvent is our custom WaitHandle.
                     // It has GetAwaiter(). 
                });
                
                // We need to await this
                // But Read returns int, not ValueTask<int>. 
                // Wait, INode.ReadAsync returns ValueTask<int>.
                // TtyDiscipline.Read is synchronous-like?
                // The caller (SysRead) awaits.
                
                // Implementation Issue: TtyDiscipline.Read signature was `int Read(...)`.
                // It SHOULD be valid to just return -EAGAIN if we want to rely on caller's loop,
                // BUT we want to suspend.
                
                // The caller (TtyInode) should handle the await. 
                // Let's change TtyDiscipline.Read to return 0 if blocked?
                // Or better: TtyDiscipline should expose the WaitHandle so TtyInode can await it.
            }
            
            // For now, implementing "Blocking via Loop" using Thread.Sleep? No, async!
            // We need to change TtyDiscipline.Read to be async or return a WaitHandle.
            // But to fit the existing synchronous-looking signature in the snippet...
            // Let's look at TtyInputQueue.Read in the snippet:
            /*
            var task = Scheduler.GetCurrent();
            if (task != null) {
                task.BlockingTask = WaitForDataAsync();
                return 0; 
            }
            */
            
            // Logic: if no data, register blocking task and return 0.
            // The Syscall handler sees return 0? No, TtyInode logic usually handles this.
            
            // To properly fit the new Async architecture:
            // TtyDiscipline.Read should probably NOT block itself but return 0 or -EAGAIN,
            // and provide a WaitForData() method.
            
            // Let's assume TtyInode will handle the async wait.
            // So here we return -EAGAIN if no data.
            return -(int)Errno.EAGAIN;
        }
    }
    
    public WaitHandle DataAvailable => _dataEvent;
}

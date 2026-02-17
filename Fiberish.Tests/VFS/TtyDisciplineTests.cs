using System.Text;
using Fiberish.Core.VFS.TTY;
using Fiberish.VFS;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fiberish.Tests.VFS;

public class TtyDisciplineTests
{
    private readonly MockTtyDriver _driver;
    private readonly MockSignalBroadcaster _broadcaster;
    private readonly TtyDiscipline _tty;

    public TtyDisciplineTests()
    {
        _driver = new MockTtyDriver();
        _broadcaster = new MockSignalBroadcaster();
        _tty = new TtyDiscipline(_driver, _broadcaster, NullLogger.Instance);
    }

    [Fact]
    public void CanonicalMode_buffers_until_newline()
    {
        var buffer = new byte[100];
        
        // Input "abc" (no newline)
        _tty.Input(Encoding.ASCII.GetBytes("abc"));
        
        // Read should return 0 (no line ready)
        // Wait, Read returns EAGAIN if no data?
        // Check TtyDiscipline implementation:
        // If queue empty and flags & O_NONBLOCK -> EAGAIN.
        // If blocking -> registers wait (in my implementation it returned EAGAIN to signify block).
        
        int read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(-(int)Fiberish.Native.Errno.EAGAIN, read);
        
        // Input newline
        _tty.Input(Encoding.ASCII.GetBytes("\n"));
        
        // Now should read "abc\n"
        read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(4, read);
        Assert.Equal("abc\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void CanonicalMode_handles_backspace()
    {
        var buffer = new byte[100];
        
        // "abc" + DEL + "d" + NL
        // Expected: "abd\n"
        // DEL is 127 by default in TtyDiscipline
        
        _tty.Input(Encoding.ASCII.GetBytes("abc"));
        _tty.Input(new byte[] { 127 }); // Backspace
        _tty.Input(Encoding.ASCII.GetBytes("d\n"));
        
        int read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(4, read);
        Assert.Equal("abd\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void RawMode_passes_data_immediately()
    {
        var buffer = new byte[100];
        
        // Switch to raw mode
        var termios = new byte[60];
        _tty.GetAttr(termios);
        // ICANON is bit 1 (value 2) in lflag
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u;
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Input "abc"
        _tty.Input(Encoding.ASCII.GetBytes("abc"));
        
        // Read "abc" immediately
        int read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(3, read);
        Assert.Equal("abc", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void CanonicalMode_handles_EOF()
    {
        var buffer = new byte[100];
        
        // Input "abc" + Ctrl-D (EOF = 4)
        _tty.Input(Encoding.ASCII.GetBytes("abc"));
        _tty.Input(new byte[] { 4 });
        
        // Read "abc"
        int read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(3, read);
        
        // Send another Ctrl-D on empty buffer to signal completion
        _tty.Input(new byte[] { 4 });
        
        // Next read should be 0 (EOF)
        read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(0, read);
    }

    [Fact]
    public void CanonicalMode_partial_read_preserves_line_status()
    {
        var buffer = new byte[2];
        
        _tty.Input(Encoding.ASCII.GetBytes("abc\n"));
        
        // Read 2 bytes ("ab")
        int read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(2, read);
        
        // Read next 2 bytes ("c\n")
        read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(2, read);
        Assert.Equal("c\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void NonBlockingRead_returns_EAGAIN_when_empty()
    {
        var buffer = new byte[100];
        int read = _tty.Read(buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(-(int)Fiberish.Native.Errno.EAGAIN, read);
    }

    [Fact]
    public void Signal_CtrlC_is_broadcast()
    {
        // Ctrl-C is 3 by default
        _tty.Input(new byte[] { 3 });
        
        Assert.Equal(2, _broadcaster.LastSignal); // SIGINT = 2
        Assert.True(_broadcaster.SignalSent);
    }

    private class MockTtyDriver : ITtyDriver
    {
        public List<byte> Output = new();

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            Output.AddRange(buffer.ToArray());
            return buffer.Length;
        }

        public void Flush() { }
    }

    private class MockSignalBroadcaster : ISignalBroadcaster
    {
        public int LastSignal { get; private set; }
        public bool SignalSent { get; private set; }

        public void SignalProcessGroup(int pgid, int signal)
        {
            LastSignal = signal;
            SignalSent = true;
        }

        public void SignalForegroundTask(int signal)
        {
            LastSignal = signal;
            SignalSent = true;
        }
    }
}

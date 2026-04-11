using System.Reflection;
using System.Text;
using Fiberish.Core;
using Fiberish.Core.VFS.TTY;
using Fiberish.Memory;
using Fiberish.Native;
using Fiberish.Syscalls;
using Fiberish.VFS;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fiberish.Tests.VFS;

public class TtyDisciplineTests
{
    private readonly MockSignalBroadcaster _broadcaster;
    private readonly MockTtyDriver _driver;
    private readonly FiberTask _task;
    private readonly TtyTaskContext _taskContext;
    private readonly TtyDiscipline _tty;

    public TtyDisciplineTests()
    {
        _taskContext = new TtyTaskContext();
        _driver = new MockTtyDriver();
        _broadcaster = new MockSignalBroadcaster();
        _tty = new TtyDiscipline(_driver, _broadcaster, NullLogger.Instance, _taskContext.Scheduler);
        _task = _taskContext.Task;
    }

    #region VREPRINT Tests

    [Fact]
    public void VREPRINT_echoes_current_line()
    {
        _driver.Output.Clear();

        // Type some characters
        _tty.Input(Encoding.ASCII.GetBytes("abc"));
        _tty.ProcessPendingInput();

        // Send VREPRINT (Ctrl-R = 18)
        _tty.Input(new byte[] { 18 });
        _tty.ProcessPendingInput();

        // Output should contain ^R, newline, and "abc"
        var output = Encoding.ASCII.GetString(_driver.Output.ToArray());
        Assert.Contains("abc", output);
    }

    #endregion

    #region ECHONL Tests

    [Fact]
    public void ECHONL_echoes_newline_when_ECHO_is_off()
    {
        _driver.Output.Clear();

        // Disable ECHO, enable ECHONL
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~8u; // ECHO off
        lflag |= 64u; // ECHONL on
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Input a newline
        _tty.Input(Encoding.ASCII.GetBytes("\n"));
        _tty.ProcessPendingInput();

        // Should still echo the newline due to ECHONL
        Assert.Contains((byte)10, _driver.Output);
    }

    #endregion

    #region ECHOCTL Tests

    [Fact]
    public void ECHOCTL_echoes_control_chars_as_caret()
    {
        _driver.Output.Clear();

        // ECHOCTL should be on by default
        // Input a control character in canonical mode (not a signal char)
        _tty.Input(new byte[] { 1 }); // Ctrl-A
        _tty.ProcessPendingInput();

        // Should echo as ^A
        Assert.Contains((byte)'^', _driver.Output);
        Assert.Contains((byte)'A', _driver.Output);
    }

    #endregion

    #region UTF-8 Erase Tests

    [Fact]
    public void CanonErase_handles_multibyte_utf8()
    {
        var buffer = new byte[100];

        // Enable IUTF8
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var iflag = BitConverter.ToUInt32(termios, 0);
        iflag |= 0x4000u; // IUTF8
        BitConverter.GetBytes(iflag).CopyTo(termios, 0);
        _tty.SetAttr(0, termios);

        // Input a 2-byte UTF-8 character followed by backspace
        // "é" in UTF-8 is 0xC3 0xA9
        _tty.Input(new byte[] { 0xC3, 0xA9 });
        _tty.Input(new byte[] { 127 }); // Backspace
        _tty.Input(Encoding.ASCII.GetBytes("\n"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        // Buffer should be empty (just newline) since we erased the UTF-8 char
        Assert.Equal(1, read);
        Assert.Equal("\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    #endregion

    #region Basic Canonical Mode Tests

    [Fact]
    public void CanonicalMode_buffers_until_newline()
    {
        var buffer = new byte[100];

        // Input "abc" (no newline)
        _tty.Input(Encoding.ASCII.GetBytes("abc"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(-(int)Errno.EAGAIN, read);

        // Input newline
        _tty.Input(Encoding.ASCII.GetBytes("\n"));

        // Now should read "abc\n"
        read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
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

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(4, read);
        Assert.Equal("abd\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void RawMode_passes_data_immediately()
    {
        var buffer = new byte[100];

        // Switch to raw mode
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        // ICANON is bit 1 (value 2) in lflag
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u;
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Input "abc"
        _tty.Input(Encoding.ASCII.GetBytes("abc"));

        // Read "abc" immediately
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
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
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(3, read);

        // Send another Ctrl-D on empty buffer to signal completion
        _tty.Input(new byte[] { 4 });

        // Next read should be 0 (EOF)
        read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(0, read);
    }

    [Fact]
    public void CanonicalMode_partial_read_preserves_line_status()
    {
        var buffer = new byte[2];

        _tty.Input(Encoding.ASCII.GetBytes("abc\n"));

        // Read 2 bytes ("ab")
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(2, read);

        // Read next 2 bytes ("c\n")
        read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(2, read);
        Assert.Equal("c\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void NonBlockingRead_returns_EAGAIN_when_empty()
    {
        var buffer = new byte[100];
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(-(int)Errno.EAGAIN, read);
    }

    #endregion

    #region Raw Mode VMIN/VTIME Tests

    [Fact]
    public void RawMode_VMIN_blocks_until_minimum_bytes()
    {
        var buffer = new byte[100];

        // Switch to raw mode and configure VMIN=2, VTIME=0
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 2; // VMIN = 2
        termios[17 + 5] = 0; // VTIME = 0
        _tty.SetAttr(0, termios);

        // Input 1 byte (less than VMIN)
        _tty.Input(new[] { (byte)'a' });

        // Read without O_NONBLOCK should return EAGAIN because we need 2 bytes
        var read = _tty.Read(_task, buffer, 0);
        Assert.Equal(-(int)Errno.EAGAIN, read);

        // Input 2nd byte
        _tty.Input(new[] { (byte)'b' });

        // Now we have 2 bytes, read should succeed
        read = _tty.Read(_task, buffer, 0);
        Assert.Equal(2, read);
        Assert.Equal("ab", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void RawMode_VMIN_VTIME_keeps_waiting_until_timeout_or_minimum()
    {
        var buffer = new byte[100];

        // Switch to raw mode and configure VMIN=3, VTIME=1 (100ms)
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 3; // VMIN = 3
        termios[17 + 5] = 1; // VTIME = 1
        _tty.SetAttr(0, termios);

        // Input 0 bytes, should block (EAGAIN to wait for IOAwaiter)
        var read = _tty.Read(_task, buffer, 0);
        Assert.Equal(-(int)Errno.EAGAIN, read);

        // Input 2 bytes (less than VMIN). Linux keeps waiting until either VMIN is
        // satisfied or the inter-byte timeout fires.
        _tty.Input(new[] { (byte)'a', (byte)'b' });

        read = _tty.Read(_task, buffer, 0);
        Assert.Equal(-(int)Errno.EAGAIN, read);
    }

    [Fact]
    public void RawMode_VTIME_only_blocks_until_data_arrives()
    {
        var buffer = new byte[100];

        // Switch to raw mode and configure VMIN=0, VTIME=5 (500ms)
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 0; // VMIN = 0
        termios[17 + 5] = 5; // VTIME = 5
        _tty.SetAttr(0, termios);

        // Input 0 bytes, should block (EAGAIN)
        var read = _tty.Read(_task, buffer, 0);
        Assert.Equal(-(int)Errno.EAGAIN, read);

        // Input 1 byte
        _tty.Input(new[] { (byte)'x' });

        read = _tty.Read(_task, buffer, 0);
        Assert.Equal(1, read);
        Assert.Equal("x", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void RawMode_MIN0_TIME0_blocking_read_returns_zero_when_no_data()
    {
        var buffer = new byte[100];

        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 0; // VMIN = 0
        termios[17 + 5] = 0; // VTIME = 0
        _tty.SetAttr(0, termios);

        var read = _tty.Read(_task, buffer, 0);
        Assert.Equal(0, read);
    }

    [Fact]
    public void RawMode_Poll_requires_vmin_bytes_when_time_is_zero()
    {
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 2; // VMIN = 2
        termios[17 + 5] = 0; // VTIME = 0
        _tty.SetAttr(0, termios);

        var sb = new TestSuperBlock();
        var inode = new ConsoleInode(sb, true, _tty);
        var file = new LinuxFile(new Dentry(FsName.FromString("stdin"), inode, null, sb), FileFlags.O_RDONLY, null!);

        try
        {
            _tty.Input([(byte)'a']);
            Assert.Equal(0, inode.Poll(file, LinuxConstants.POLLIN));

            _tty.Input([(byte)'b']);
            Assert.Equal(LinuxConstants.POLLIN, inode.Poll(file, LinuxConstants.POLLIN));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void RawMode_Poll_with_min0_time0_requires_a_byte()
    {
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 0; // VMIN = 0
        termios[17 + 5] = 0; // VTIME = 0
        _tty.SetAttr(0, termios);

        var sb = new TestSuperBlock();
        var inode = new ConsoleInode(sb, true, _tty);
        var file = new LinuxFile(new Dentry(FsName.FromString("stdin"), inode, null, sb), FileFlags.O_RDONLY, null!);

        try
        {
            Assert.Equal(0, inode.Poll(file, LinuxConstants.POLLIN));

            _tty.Input([(byte)'a']);
            Assert.Equal(LinuxConstants.POLLIN, inode.Poll(file, LinuxConstants.POLLIN));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void RawMode_Poll_with_vtime_uses_single_byte_threshold()
    {
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 3; // VMIN = 3
        termios[17 + 5] = 1; // VTIME = 1
        _tty.SetAttr(0, termios);

        var sb = new TestSuperBlock();
        var inode = new ConsoleInode(sb, true, _tty);
        var file = new LinuxFile(new Dentry(FsName.FromString("stdin"), inode, null, sb), FileFlags.O_RDONLY, null!);

        try
        {
            _tty.Input([(byte)'a']);
            Assert.Equal(LinuxConstants.POLLIN, inode.Poll(file, LinuxConstants.POLLIN));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void RawMode_RegisterWait_does_not_fire_before_vmin_threshold()
    {
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        termios[17 + 6] = 2; // VMIN = 2
        termios[17 + 5] = 0; // VTIME = 0
        _tty.SetAttr(0, termios);

        var sb = new TestSuperBlock();
        var inode = new ConsoleInode(sb, true, _tty);
        var file = new LinuxFile(new Dentry(FsName.FromString("stdin"), inode, null, sb), FileFlags.O_RDONLY, null!);

        try
        {
            _tty.Input([(byte)'a']);

            var fired = 0;
            using var reg = inode.RegisterWaitHandle(file, _task, () => fired++, LinuxConstants.POLLIN);
            _taskContext.DrainEvents();

            Assert.NotNull(reg);
            Assert.Equal(0, fired);

            _tty.Input([(byte)'b']);
            _taskContext.DrainEvents();

            Assert.True(fired > 0);
        }
        finally
        {
            file.Close();
        }
    }

    #endregion

    #region Signal Tests

    [Fact]
    public void Signal_CtrlC_is_broadcast()
    {
        // Ctrl-C is 3 by default
        _tty.Input(new byte[] { 3 });
        _tty.ProcessPendingInput();

        Assert.Equal(2, _broadcaster.LastSignal); // SIGINT = 2
        Assert.True(_broadcaster.SignalSent);
    }

    [Fact]
    public void Signal_CtrlBackslash_sends_SIGQUIT()
    {
        // Ctrl-\ is 28 by default
        _tty.Input(new byte[] { 28 });
        _tty.ProcessPendingInput();

        Assert.Equal(3, _broadcaster.LastSignal); // SIGQUIT = 3
        Assert.True(_broadcaster.SignalSent);
    }

    [Fact]
    public void Signal_CtrlZ_sends_SIGTSTP()
    {
        // Ctrl-Z is 26 by default
        _tty.Input(new byte[] { 26 });
        _tty.ProcessPendingInput(); // Process the queued input

        Assert.Equal(20, _broadcaster.LastSignal); // SIGTSTP = 20
        Assert.True(_broadcaster.SignalSent);
    }

    [Fact]
    public void Signal_NOFLSH_preserves_buffer()
    {
        var buffer = new byte[100];

        // Set NOFLSH flag and add some data
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag |= 128u; // NOFLSH
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Add some data to canonical buffer
        _tty.Input(Encoding.ASCII.GetBytes("test"));
        _tty.ProcessPendingInput();

        // Send SIGINT (Ctrl-C)
        _tty.Input(new byte[] { 3 });
        _tty.ProcessPendingInput();

        Assert.Equal(2, _broadcaster.LastSignal);

        // With NOFLSH, the buffer should still contain "test"
        // Add newline to flush
        _tty.Input(Encoding.ASCII.GetBytes("\n"));
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(5, read);
        Assert.Equal("test\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void Signal_without_NOFLSH_clears_buffer()
    {
        var buffer = new byte[100];

        // NOFLSH is NOT set by default
        // Add some data to canonical buffer
        _tty.Input(Encoding.ASCII.GetBytes("test"));
        _tty.ProcessPendingInput();

        // Send SIGINT (Ctrl-C)
        _tty.Input(new byte[] { 3 });
        _tty.ProcessPendingInput();

        Assert.Equal(2, _broadcaster.LastSignal);

        // Without NOFLSH, the buffer should be cleared
        // Add newline to try to flush
        _tty.Input(Encoding.ASCII.GetBytes("\n"));
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(1, read);
        Assert.Equal("\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void ConsolePoll_processes_VINTR_and_broadcasts_SIGINT_without_read()
    {
        using var taskContext = new TtyTaskContext();
        var driver = new MockTtyDriver();
        var broadcaster = new MockSignalBroadcaster();
        var tty = new TtyDiscipline(driver, broadcaster, NullLogger.Instance, taskContext.Scheduler);
        var sb = new TestSuperBlock();
        var inode = new ConsoleInode(sb, true, tty);
        var file = new LinuxFile(new Dentry(FsName.FromString("stdin"), inode, null, sb), FileFlags.O_RDONLY, null!);

        try
        {
            tty.Input(new byte[] { 3 }); // VINTR (Ctrl-C)

            const short POLLIN = 0x0001;
            var revents = inode.Poll(file, POLLIN);

            Assert.True(broadcaster.SignalSent);
            Assert.Equal(2, broadcaster.LastSignal); // SIGINT
            Assert.Equal(0, revents); // No readable data from VINTR itself
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void ConsolePoll_output_reflects_tty_writability()
    {
        using var taskContext = new TtyTaskContext();
        var driver = new ControlledWriteTtyDriver();
        var broadcaster = new MockSignalBroadcaster();
        var tty = new TtyDiscipline(driver, broadcaster, NullLogger.Instance, taskContext.Scheduler);
        var sb = new TestSuperBlock();
        var inode = new ConsoleInode(sb, false, tty);
        var file = new LinuxFile(new Dentry(FsName.FromString("stdout"), inode, null, sb), FileFlags.O_WRONLY, null!);

        try
        {
            const short POLLOUT = 0x0004;
            driver.SetWritable(false);
            Assert.Equal(0, inode.Poll(file, POLLOUT));

            driver.SetWritable(true);
            Assert.Equal(POLLOUT, inode.Poll(file, POLLOUT));
        }
        finally
        {
            file.Close();
        }
    }

    [Fact]
    public void ConsoleRegisterWait_output_wakes_when_writable()
    {
        using var taskContext = new TtyTaskContext();
        var driver = new ControlledWriteTtyDriver();
        var broadcaster = new MockSignalBroadcaster();
        var tty = new TtyDiscipline(driver, broadcaster, NullLogger.Instance, taskContext.Scheduler);

        driver.SetWritable(false);
        var fired = false;
        var registered = tty.RegisterWriteWait(() => fired = true, taskContext.Task.CommonKernel);
        Assert.True(registered);
        Assert.False(fired);

        driver.SetWritable(true);
        driver.NotifyWritable();
        Assert.True(fired);
    }

    #endregion

    #region Flow Control Tests (IXON/IXOFF)

    [Fact]
    public void FlowControl_VSTOP_stops_output()
    {
        _driver.Output.Clear();

        // Write something to trigger output processing
        _tty.Write(_task, Encoding.ASCII.GetBytes("hello"));

        // Should have output
        Assert.True(_driver.Output.Count > 0);
        _driver.Output.Clear();

        // Send VSTOP (Ctrl-S = 19)
        _tty.Input(new byte[] { 19 });

        // Try to write - should be blocked/stopped
        _tty.Write(_task, Encoding.ASCII.GetBytes("world"));

        // Output should be empty or same length because output is stopped
        // Note: Current implementation discards output when stopped
    }

    [Fact]
    public void FlowControl_VSTART_restarts_output()
    {
        _driver.Output.Clear();

        // Send VSTOP (Ctrl-S = 19)
        _tty.Input(new byte[] { 19 });

        // Send VSTART (Ctrl-Q = 17)
        _tty.Input(new byte[] { 17 });

        // Now output should work again
        _tty.Write(_task, Encoding.ASCII.GetBytes("test"));

        // Should have output
        Assert.True(_driver.Output.Count > 0);
    }

    [Fact]
    public void FlowControl_VSTOP_VSTART_dont_appear_in_input()
    {
        var buffer = new byte[100];

        // Send VSTOP and VSTART
        _tty.Input(new byte[] { 19 }); // Ctrl-S
        _tty.Input(new byte[] { 17 }); // Ctrl-Q

        // These should not appear in input queue
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(-(int)Errno.EAGAIN, read);
    }

    #endregion

    #region LNEXT (Literal Next) Tests

    [Fact]
    public void LNEXT_allows_literal_control_characters()
    {
        var buffer = new byte[100];

        // Ctrl-V (22) followed by Ctrl-C (3) should insert literal 3
        _tty.Input(new byte[] { 22, 3 });
        _tty.Input(Encoding.ASCII.GetBytes("\n"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(2, read);
        Assert.Equal(3, buffer[0]); // Literal Ctrl-C
        Assert.Equal(10, buffer[1]); // Newline
    }

    [Fact]
    public void LNEXT_does_not_trigger_signal()
    {
        // Create a fresh TTY to avoid signal state from other tests
        using var taskContext = new TtyTaskContext();
        var driver = new MockTtyDriver();
        var broadcaster = new MockSignalBroadcaster();
        var tty = new TtyDiscipline(driver, broadcaster, NullLogger.Instance, taskContext.Scheduler);

        // Ctrl-V (22) followed by Ctrl-C (3) should NOT send signal
        tty.Input(new byte[] { 22, 3 });

        Assert.False(broadcaster.SignalSent);
    }

    #endregion

    #region VWERASE (Word Erase) Tests

    [Fact]
    public void VWERASE_erases_last_word()
    {
        var buffer = new byte[100];

        // Type "hello world" then VWERASE (Ctrl-W = 23)
        _tty.Input(Encoding.ASCII.GetBytes("hello world"));
        _tty.Input(new byte[] { 23 }); // VWERASE
        _tty.Input(Encoding.ASCII.GetBytes("\n"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        // "hello " (with trailing space) + "\n" = 7 characters
        Assert.Equal(7, read);
        Assert.Equal("hello \n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void VWERASE_erases_trailing_whitespace()
    {
        var buffer = new byte[100];

        // Type "hello   " (with trailing spaces) then VWERASE
        _tty.Input(Encoding.ASCII.GetBytes("hello   "));
        _tty.Input(new byte[] { 23 }); // VWERASE
        _tty.Input(Encoding.ASCII.GetBytes("\n"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(1, read);
        Assert.Equal("\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    #endregion

    #region Input Flag Tests

    [Fact]
    public void IGNCR_ignores_carriage_return()
    {
        var buffer = new byte[100];

        // Enable IGNCR
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var iflag = BitConverter.ToUInt32(termios, 0);
        iflag |= 0x80u; // IGNCR
        // Disable ICRNL to avoid CR->NL conversion
        iflag &= ~0x100u; // ICRNL
        BitConverter.GetBytes(iflag).CopyTo(termios, 0);
        _tty.SetAttr(0, termios);

        // Input "he\rllo\n" - CR should be ignored
        _tty.Input(Encoding.ASCII.GetBytes("he\rllo\n"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        // "hello\n" = 6 characters (CR is ignored)
        Assert.Equal(6, read);
        Assert.Equal("hello\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void INLCR_maps_newline_to_carriage_return()
    {
        var buffer = new byte[100];

        // Enable INLCR, disable ICRNL
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var iflag = BitConverter.ToUInt32(termios, 0);
        iflag |= 0x40u; // INLCR
        iflag &= ~0x100u; // ICRNL (off)
        iflag &= ~0x80u; // IGNCR (off)
        BitConverter.GetBytes(iflag).CopyTo(termios, 0);
        _tty.SetAttr(0, termios);

        // Switch to raw mode to see the actual characters
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Input "a\nb"
        _tty.Input(Encoding.ASCII.GetBytes("a\nb"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(3, read);
        Assert.Equal("a\rb", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void ISTRIP_strips_eighth_bit()
    {
        var buffer = new byte[100];

        // Enable ISTRIP
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var iflag = BitConverter.ToUInt32(termios, 0);
        iflag |= 0x20u; // ISTRIP
        BitConverter.GetBytes(iflag).CopyTo(termios, 0);
        // Switch to raw mode
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Input byte with 8th bit set (0x80 + 0x41 = 0xC1 = 'A' with high bit)
        _tty.Input(new byte[] { 0xC1 });

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(1, read);
        // 0xC1 & 0x7F = 0x41 = 'A'
        Assert.Equal((byte)'A', buffer[0]);
    }

    [Fact]
    public void IUCLC_maps_uppercase_to_lowercase()
    {
        var buffer = new byte[100];

        // Enable IUCLC
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var iflag = BitConverter.ToUInt32(termios, 0);
        iflag |= 0x200u; // IUCLC (0x200)
        BitConverter.GetBytes(iflag).CopyTo(termios, 0);

        // Switch to raw mode to avoid canonical buffering
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        // Input uppercase and lowercase letters
        _tty.Input(Encoding.ASCII.GetBytes("Hello WORLD!"));

        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(12, read);
        Assert.Equal("hello world!", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void IMAXBEL_rings_bell_when_buffer_full()
    {
        var buffer = new byte[4096]; // Read buffer

        // Enable IMAXBEL
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var iflag = BitConverter.ToUInt32(termios, 0);
        iflag |= 0x2000u; // IMAXBEL (0x2000)
        BitConverter.GetBytes(iflag).CopyTo(termios, 0);

        // Ensure canonical mode is ON (to use _canonBuffer)
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag |= 2u; // ICANON on
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        _driver.Output.Clear();

        // Fill canonical buffer (capacity is hardcoded to 4096)
        var chunk = new byte[1024];
        Array.Fill(chunk, (byte)'a');
        _tty.Input(chunk);
        _tty.Input(chunk);
        _tty.Input(chunk);
        _tty.Input(chunk);
        _tty.ProcessPendingInput(); // Fill up to 4096

        // Buffer is now full, next char should trigger BEL
        _driver.Output.Clear();
        _tty.Input(new[] { (byte)'b' });
        _tty.ProcessPendingInput();

        // Should output BEL (7) or ^G if ECHOCTL is on. 
        // In our case ECHOCTL is ON, so BEL (0x07) echoes as ^G (0x5E 0x47)
        // Let's just check that it outputs BEL or ^G
        var strOut = Encoding.ASCII.GetString(_driver.Output.ToArray());
        Assert.True(_driver.Output.Contains(7) || strOut.Contains("^G"));

        // At this point, _canonBuffer has 4096 'a's.
        // The read without newline returns EAGAIN
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(-(int)Errno.EAGAIN, read);

        // Remove one character with backspace so we can add a newline
        _tty.Input(new byte[] { 127 }); // Backspace (VERASE)
        _tty.ProcessPendingInput();

        // Input newline to flush the canonical buffer
        _tty.Input(new byte[] { 10 }); // Newline
        _tty.ProcessPendingInput();

        // Now we can read the 4095 'a's + newline
        read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(4096, read); // 4095 'a's + '\n'
        Assert.DoesNotContain((byte)'b', buffer.AsSpan(0, read).ToArray());
    }

    #endregion

    #region Output Flag Tests

    [Fact]
    public void OCRNL_maps_CR_to_NL()
    {
        _driver.Output.Clear();

        // Enable OCRNL
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var oflag = BitConverter.ToUInt32(termios, 4);
        oflag |= 8u; // OCRNL
        // Disable ONLCR to avoid NL->CRNL
        oflag &= ~4u; // ONLCR
        BitConverter.GetBytes(oflag).CopyTo(termios, 4);
        _tty.SetAttr(0, termios);

        // Write CR
        _tty.Write(_task, new byte[] { 13 });

        // Should output NL (10)
        Assert.Single(_driver.Output);
        Assert.Equal(10, _driver.Output[0]);
    }

    [Fact]
    public void ONLRET_nl_performs_cr_function()
    {
        _driver.Output.Clear();

        // Enable ONLRET
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var oflag = BitConverter.ToUInt32(termios, 4);
        oflag |= 32u; // ONLRET
        oflag &= ~4u; // ONLCR off
        BitConverter.GetBytes(oflag).CopyTo(termios, 4);
        _tty.SetAttr(0, termios);

        // Write NL
        _tty.Write(_task, new byte[] { 10 });

        // Should output just NL (not CR-NL)
        Assert.Single(_driver.Output);
        Assert.Equal(10, _driver.Output[0]);
    }

    [Fact]
    public void OLCUC_maps_lowercase_to_uppercase()
    {
        _driver.Output.Clear();

        // Enable OLCUC
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var oflag = BitConverter.ToUInt32(termios, 4);
        oflag |= 2u; // OLCUC (2)
        BitConverter.GetBytes(oflag).CopyTo(termios, 4);
        _tty.SetAttr(0, termios);

        // Write lowercase and uppercase
        _tty.Write(_task, Encoding.ASCII.GetBytes("hello WORLD!"));

        // Should output "HELLO WORLD!"
        var output = Encoding.ASCII.GetString(_driver.Output.ToArray());
        Assert.Equal("HELLO WORLD!", output);
    }

    #endregion

    #region Thread Safety and Race Condition Tests

    [Fact]
    public void Input_enqueues_to_device_buffer_not_processes_directly()
    {
        // On the scheduler thread, Input() is processed immediately into the line discipline.
        _tty.Input(Encoding.ASCII.GetBytes("abc\n"));

        Assert.False(_tty.Device.HasInterrupt);
        Assert.True(_tty.HasDataAvailable);
    }

    [Fact]
    public void Input_signals_DataAvailable_when_data_arrives()
    {
        // DataAvailable should not be signaled initially
        Assert.False(_tty.DataAvailable.IsSignaled);

        // Scheduler-thread input is processed immediately and signals DataAvailable.
        _tty.Input(Encoding.ASCII.GetBytes("test\n"));
        Assert.True(_tty.DataAvailable.IsSignaled);
    }

    [Fact]
    public void Read_processes_pending_input_from_device()
    {
        var buffer = new byte[100];

        // Input data (goes to device buffer)
        _tty.Input(Encoding.ASCII.GetBytes("hello\n"));

        // Read should process the pending input and return the data
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(6, read);
        Assert.Equal("hello\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void HasDataAvailable_checks_both_device_and_queue()
    {
        // Initially no data
        Assert.False(_tty.HasDataAvailable);

        // Input data (goes to device buffer)
        _tty.Input(Encoding.ASCII.GetBytes("test\n"));

        // HasDataAvailable should be true (device has data)
        Assert.True(_tty.HasDataAvailable);

        // Process the input
        _tty.Read(_task, new byte[100], FileFlags.O_NONBLOCK);

        // After processing, device is empty but we need to check the race condition scenario
        // where new data might arrive between Read checking queue and resetting event
    }

    [Fact]
    public void HasDataAvailable_is_true_when_device_buffer_has_pending_raw_input()
    {
        var isInsideRunLoopField = typeof(KernelScheduler).GetField("_isInsideRunLoop",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(isInsideRunLoopField);

        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        isInsideRunLoopField!.SetValue(_taskContext.Scheduler, false);
        try
        {
            _tty.Input(Encoding.ASCII.GetBytes("x"));

            Assert.True(_tty.Device.HasBufferedInput);
            Assert.True(_tty.HasDataAvailable);

            var buffer = new byte[8];
            var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
            Assert.Equal(1, read);
            Assert.Equal("x", Encoding.ASCII.GetString(buffer, 0, read));
        }
        finally
        {
            isInsideRunLoopField.SetValue(_taskContext.Scheduler, true);
        }
    }

    [Fact]
    public void No_lost_wakeup_when_data_arrives_during_read()
    {
        // This test simulates the race condition scenario:
        // 1. Read() checks queue (empty)
        // 2. Input() enqueues data
        // 3. Read() resets event
        // 4. Task awaits (should not miss the signal)

        var buffer = new byte[100];

        // First, verify DataAvailable is signaled after pending input is processed
        Assert.False(_tty.DataAvailable.IsSignaled);
        _tty.Input(Encoding.ASCII.GetBytes("data\n"));
        _tty.ProcessPendingInput();
        Assert.True(_tty.DataAvailable.IsSignaled);

        // Read should succeed and DataAvailable should still be signaled for next read
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(5, read);

        // After read, event might be reset, but if more data arrives, it should be signaled again
        _tty.Input(Encoding.ASCII.GetBytes("more\n"));
        _tty.ProcessPendingInput();
        Assert.True(_tty.DataAvailable.IsSignaled);
    }

    [Fact]
    public void Concurrent_input_from_multiple_calls_queued_correctly()
    {
        var buffer = new byte[100];

        // Simulate multiple rapid input calls (like from background thread)
        _tty.Input(Encoding.ASCII.GetBytes("a"));
        _tty.Input(Encoding.ASCII.GetBytes("b"));
        _tty.Input(Encoding.ASCII.GetBytes("c"));
        _tty.Input(Encoding.ASCII.GetBytes("\n"));

        // Scheduler-thread input is immediately drained from the device into the discipline queue.
        Assert.False(_tty.Device.HasInterrupt);
        Assert.True(_tty.HasDataAvailable);

        // Read should process all and return "abc\n"
        var read = _tty.Read(_task, buffer, FileFlags.O_NONBLOCK);
        Assert.Equal(4, read);
        Assert.Equal("abc\n", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void Input_is_bounded_by_64k_ring_buffer()
    {
        // Switch to raw mode so input queue reflects bytes directly.
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        var payload = new byte[TtyDevice.DefaultInputCapacityBytes + 1024];
        Array.Fill(payload, (byte)'x');

        var accepted = _tty.Input(payload);
        Assert.Equal(TtyDevice.DefaultInputCapacityBytes, accepted);

        var totalRead = 0;
        var readBuf = new byte[8192];
        while (true)
        {
            var n = _tty.Read(_task, readBuf, FileFlags.O_NONBLOCK);
            if (n <= 0)
                break;
            totalRead += n;
        }

        Assert.Equal(TtyDevice.DefaultInputCapacityBytes, totalRead);
    }

    #endregion

    #region Helper Classes

    private class MockTtyDriver : ITtyDriver
    {
        public readonly List<byte> Output = new();

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            Output.AddRange(buffer.ToArray());
            return buffer.Length;
        }

        public void Flush()
        {
        }

        public bool CanWrite => true;

        public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
        {
            _ = scheduler;
            return false;
        }
    }

    private sealed class ControlledWriteTtyDriver : ITtyDriver
    {
        private Action? _waiter;
        private volatile bool _writable = true;

        public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
        {
            return _writable ? buffer.Length : -(int)Errno.EAGAIN;
        }

        public void Flush()
        {
        }

        public bool CanWrite => _writable;

        public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
        {
            _ = scheduler;
            if (_writable)
                return false;
            _waiter = callback;
            return true;
        }

        public void SetWritable(bool writable)
        {
            _writable = writable;
        }

        public void NotifyWritable()
        {
            _waiter?.Invoke();
            _waiter = null;
        }
    }

    private class MockSignalBroadcaster : ISignalBroadcaster
    {
        public int LastSignal { get; private set; }
        public bool SignalSent { get; private set; }

        public void SignalProcessGroup(FiberTask? task, int pgid, int signal)
        {
            LastSignal = signal;
            SignalSent = true;
        }

        public void SignalForegroundTask(FiberTask? task, int signal)
        {
            LastSignal = signal;
            SignalSent = true;
        }
    }

    private sealed class TestSuperBlock : SuperBlock
    {
        public TestSuperBlock() : base(null, new MemoryRuntimeContext())
        {
        }

        public override Inode AllocInode()
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public void BackgroundRead_with_explicit_task_sends_SIGTTIN()
    {
        _task.Process.PGID = 200;
        _tty.ForegroundPgrp = 100;

        var rc = _tty.Read(_task, new byte[16], FileFlags.O_NONBLOCK);

        Assert.Equal(-(int)Errno.ERESTARTSYS, rc);
        Assert.True(_broadcaster.SignalSent);
        Assert.Equal(21, _broadcaster.LastSignal);
    }

    [Fact]
    public void Hangup_SignalsSessionLeaderAndForegroundProcessGroup_AndClearsControllingTty()
    {
        _task.Process.SID = _task.Process.TGID;
        _task.Process.PGID = _task.Process.TGID;
        _task.Process.ControllingTty = _tty;
        _tty.SessionId = _task.Process.TGID;
        _tty.ForegroundPgrp = 200;

        var runtime = new TestRuntimeFactory();
        using var fgEngine = runtime.CreateEngine();
        var fgMemory = runtime.CreateAddressSpace();
        var fgProcess = new Process(200, fgMemory, new SyscallManager(fgEngine, fgMemory, 0))
        {
            SID = _task.Process.SID,
            PGID = 200,
            ControllingTty = _tty
        };
        _taskContext.Scheduler.RegisterProcess(fgProcess);
        var fgTask = new FiberTask(200, fgProcess, fgEngine, _taskContext.Scheduler);

        _tty.Hangup();

        var hupMask = 1UL << ((int)Signal.SIGHUP - 1);
        var contMask = 1UL << ((int)Signal.SIGCONT - 1);

        Assert.NotEqual(0UL, _task.GetVisiblePendingSignals() & hupMask);
        Assert.NotEqual(0UL, fgTask.GetVisiblePendingSignals() & hupMask);
        Assert.NotEqual(0UL, fgTask.GetVisiblePendingSignals() & contMask);
        Assert.Null(_task.Process.ControllingTty);
        Assert.Null(fgProcess.ControllingTty);
        Assert.Equal(0, _tty.SessionId);
        Assert.Equal(0, _tty.ForegroundPgrp);
    }

    [Fact]
    public void BackgroundWrite_with_TOSTOP_and_explicit_task_sends_SIGTTOU()
    {
        _task.Process.PGID = 200;
        _tty.ForegroundPgrp = 100;

        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag |= 256u; // TOSTOP
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        var rc = _tty.Write(_task, Encoding.ASCII.GetBytes("x"));

        Assert.Equal(-(int)Errno.ERESTARTSYS, rc);
        Assert.True(_broadcaster.SignalSent);
        Assert.Equal(22, _broadcaster.LastSignal);
    }

    [Fact]
    public void ControllingTtyInode_Poll_without_controlling_tty_reports_hup_and_err()
    {
        var inode = new ControllingTtyInode(_taskContext.SyscallManager.MemfdSuperBlock);
        var file = new LinuxFile(new Dentry(FsName.FromString("tty"), inode, null, _taskContext.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, _taskContext.SyscallManager.AnonMount);

        var revents = ((ITaskPollSource)inode).Poll(file, _task, LinuxConstants.POLLIN | LinuxConstants.POLLOUT);

        Assert.True((revents & PollEvents.POLLHUP) != 0);
        Assert.True((revents & PollEvents.POLLERR) != 0);
    }

    [Fact]
    public void ControllingTtyInode_RegisterWaitHandle_resets_stale_signal_without_spurious_callback()
    {
        _task.Process.ControllingTty = _tty;

        var inode = new ControllingTtyInode(_taskContext.SyscallManager.MemfdSuperBlock);
        var file = new LinuxFile(new Dentry(FsName.FromString("tty"), inode, null, _taskContext.SyscallManager.MemfdSuperBlock),
            FileFlags.O_RDWR, _taskContext.SyscallManager.AnonMount);

        Assert.False(_tty.HasDataAvailable);
        _tty.DataAvailable.Signal();
        Assert.True(_tty.DataAvailable.IsSignaled);

        var fired = 0;
        using var reg = inode.RegisterWaitHandle(file, _task, () => fired++, LinuxConstants.POLLIN);

        _taskContext.DrainEvents();

        Assert.NotNull(reg);
        Assert.False(_tty.DataAvailable.IsSignaled);
        Assert.Equal(0, fired);

        _tty.Input(Encoding.ASCII.GetBytes("x\n"));
        _taskContext.DrainEvents();

        Assert.True(fired > 0);
    }

    [Fact]
    public void QueueReadinessRegistration_preserves_fresh_tty_signal_that_arrives_after_watch_creation()
    {
        var termios = new byte[LinuxConstants.TERMIOS_SIZE_I386];
        _tty.GetAttr(termios);
        var lflag = BitConverter.ToUInt32(termios, 12);
        lflag &= ~2u; // ICANON off
        BitConverter.GetBytes(lflag).CopyTo(termios, 12);
        _tty.SetAttr(0, termios);

        var watch = new QueueReadinessWatch(LinuxConstants.POLLIN, () => _tty.HasDataAvailable, _tty.DataAvailable,
            _tty.DataAvailable.Reset);

        Assert.False(_tty.HasDataAvailable);
        Assert.False(_tty.DataAvailable.IsSignaled);

        _tty.Input(Encoding.ASCII.GetBytes("x"));
        _taskContext.DrainEvents();

        Assert.True(_tty.HasDataAvailable);
        Assert.True(_tty.DataAvailable.IsSignaled);

        var fired = 0;
        using var reg = QueueReadinessRegistration.RegisterHandle(() => fired++, _task, LinuxConstants.POLLIN, watch);

        _taskContext.DrainEvents();

        Assert.NotNull(reg);
        Assert.True(_tty.DataAvailable.IsSignaled);
        Assert.True(fired > 0);
    }

    private sealed class TtyTaskContext : IDisposable
    {
        private static readonly MethodInfo DrainEventsMethod =
            typeof(KernelScheduler).GetMethod("DrainEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public TtyTaskContext()
        {
            var runtime = new TestRuntimeFactory();
            Engine = runtime.CreateEngine();
            Memory = runtime.CreateAddressSpace();
            SyscallManager = new SyscallManager(Engine, Memory, 0);
            Scheduler = new KernelScheduler();
            Process = new Process(1234, Memory, SyscallManager);
            Scheduler.RegisterProcess(Process);
            Task = new FiberTask(1234, Process, Engine, Scheduler);
            Engine.Owner = Task;
            Scheduler.CurrentTask = Task;
            Process.PGID = 100;
            Process.SID = 100;
        }

        public Engine Engine { get; }
        public VMAManager Memory { get; }
        public SyscallManager SyscallManager { get; }
        public KernelScheduler Scheduler { get; }
        public Process Process { get; }
        public FiberTask Task { get; }

        public void Dispose()
        {
            SyscallManager.Close();
            GC.KeepAlive(Task);
        }

        public void DrainEvents()
        {
            _ = (bool)DrainEventsMethod.Invoke(Scheduler, null)!;
        }
    }

    #endregion
}

using Fiberish.Native;
using Fiberish.VFS;
using Microsoft.Extensions.Logging;

namespace Fiberish.Core.VFS.TTY;

/// <summary>
///     Represents the master side of a PTY pair.
///     The master is opened via /dev/ptmx and is used to read/write
///     data to/from the slave terminal.
/// </summary>
public class PtyMaster
{
    private readonly ILogger _logger;
    private readonly PtyPair _pair;

    public PtyMaster(PtyPair pair, ILogger logger)
    {
        _pair = pair;
        _logger = logger;
        OutputBuffer = new PtyBuffer();
        InputBuffer = new PtyBuffer();
    }

    /// <summary>
    ///     Buffer for data written by master (to be read by slave).
    /// </summary>
    public PtyBuffer InputBuffer { get; }

    /// <summary>
    ///     Buffer for data written by slave (to be read by master).
    /// </summary>
    public PtyBuffer OutputBuffer { get; }

    /// <summary>
    ///     Checks if there is data available to read from the slave.
    /// </summary>
    public bool HasDataAvailable => OutputBuffer.HasData;

    /// <summary>
    ///     Gets the wait queue for data availability.
    /// </summary>
    public AsyncWaitQueue DataAvailable => OutputBuffer.DataAvailable;

    /// <summary>
    ///     Reads data from the slave (output from the slave's perspective).
    /// </summary>
    public int Read(Span<byte> buffer, FileFlags flags)
    {
        if ((flags & FileFlags.O_NONBLOCK) != 0 && !OutputBuffer.HasData)
            return -(int)Errno.EAGAIN;

        return OutputBuffer.Read(buffer);
    }

    /// <summary>
    ///     Writes data to the slave (input from the slave's perspective).
    /// </summary>
    public int Write(ReadOnlySpan<byte> buffer)
    {
        return InputBuffer.Write(buffer);
    }

    /// <summary>
    ///     Handles ioctl requests for the master side.
    /// </summary>
    public int Ioctl(FiberTask task, uint request, uint arg)
    {
        var engine = task.CPU;
        switch (request)
        {
            case LinuxConstants.TIOCGPTN:
                // Get PTY number
                var index = (uint)_pair.Index;
                if (!engine.CopyToUser(arg, BitConverter.GetBytes(index)))
                    return -(int)Errno.EFAULT;
                _logger.LogInformation("[PtyMaster] TIOCGPTN: returning index={Index}", _pair.Index);
                return 0;

            case LinuxConstants.TIOCSPTLCK:
                // Lock/unlock PTY
                var lockBuffer = new byte[1];
                if (!engine.CopyFromUser(arg, lockBuffer))
                    return -(int)Errno.EFAULT;
                _pair.SetLocked(lockBuffer[0] != 0);
                _logger.LogInformation("[PtyMaster] TIOCSPTLCK: locked={Locked}", _pair.IsLocked);
                return 0;

            case LinuxConstants.TIOCGPTLCK:
                // Get lock status
                var isLocked = _pair.IsLocked ? (byte)1 : (byte)0;
                if (!engine.CopyToUser(arg, new[] { isLocked }))
                    return -(int)Errno.EFAULT;
                return 0;

            default:
                _logger.LogWarning("[PtyMaster] Unknown ioctl request: 0x{Request:X}", request);
                return -(int)Errno.ENOTTY;
        }
    }
}

/// <summary>
///     Represents the slave side of a PTY pair.
///     The slave is accessed via /dev/pts/N and behaves like a terminal.
/// </summary>
public class PtySlave
{
    private readonly ILogger _logger;
    private readonly PtyPair _pair;

    public PtySlave(PtyPair pair, ILogger logger)
    {
        _pair = pair;
        _logger = logger;
    }

    /// <summary>
    ///     The TTY discipline for this slave, if initialized.
    /// </summary>
    public TtyDiscipline? Discipline { get; private set; }

    /// <summary>
    ///     Checks if there is data available to read.
    /// </summary>
    public bool HasDataAvailable
    {
        get
        {
            if (Discipline != null)
                return Discipline.HasDataAvailable;
            return _pair.Master.InputBuffer.HasData;
        }
    }

    /// <summary>
    ///     Gets the wait queue for data availability.
    /// </summary>
    public AsyncWaitQueue DataAvailable
    {
        get
        {
            if (Discipline != null)
                return Discipline.DataAvailable;
            return _pair.Master.InputBuffer.DataAvailable;
        }
    }

    /// <summary>
    ///     Gets or creates the TTY discipline for this slave.
    /// </summary>
    public TtyDiscipline GetOrCreateDiscipline(ISignalBroadcaster broadcaster, ILogger logger)
    {
        if (Discipline != null) return Discipline;

        // Create a PTY driver that connects to the master
        var driver = new PtySlaveDriver(_pair.Master, logger);
        Discipline = new TtyDiscipline(driver, broadcaster, logger);
        return Discipline;
    }

    /// <summary>
    ///     Reads data from the PTY (input from master).
    /// </summary>
    public int Read(Span<byte> buffer, FileFlags flags)
    {
        if (Discipline != null)
            return Discipline.Read(null, buffer, flags);

        // Direct read from master's input buffer
        return _pair.Master.InputBuffer.Read(buffer);
    }

    /// <summary>
    ///     Writes data to the PTY (output to master).
    /// </summary>
    public int Write(ReadOnlySpan<byte> buffer)
    {
        if (Discipline != null)
            return Discipline.Write(null, buffer);

        // Direct write to master's output buffer
        return _pair.Master.OutputBuffer.Write(buffer);
    }
}

/// <summary>
///     ITtyDriver implementation for PTY slave.
///     Routes output from the slave to the master's output buffer.
/// </summary>
public class PtySlaveDriver : ITtyDriver
{
    private readonly ILogger _logger;
    private readonly PtyMaster _master;

    public PtySlaveDriver(PtyMaster master, ILogger logger)
    {
        _master = master;
        _logger = logger;
    }

    public int Write(TtyEndpointKind kind, ReadOnlySpan<byte> buffer)
    {
        // Write slave output to master's output buffer
        return _master.OutputBuffer.Write(buffer);
    }

    public bool CanWrite => true;

    public bool RegisterWriteWait(Action callback, KernelScheduler scheduler)
    {
        _ = scheduler;
        return false;
    }

    public void Flush()
    {
        // No-op for PTY
    }
}

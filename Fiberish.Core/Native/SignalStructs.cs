namespace Fiberish.Native;

// Reference: siginfo_t from Linux kernel
public struct SigInfo
{
    public int Signo;
    public int Errno;
    public int Code;

    // Payload (Depends on the signal type)
    // POSIX says many of these are unionized. We'll flatten them for C# ease of use.
    public int Pid;
    public uint Uid;
    public int Status;
    public long Utime;
    public long Stime;

    // sigval payload (often a ptr or an int)
    public ulong Value;

    // POSIX timers
    public int TimerId;
    public int Overrun;

    // SIGPOLL
    public long Band;
    public int Fd;
}
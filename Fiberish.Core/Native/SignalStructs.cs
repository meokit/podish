namespace Fiberish.Native;

// Reference: siginfo_t from Linux kernel
public struct SigInfo
{
    public int Signo;
    public int Errno;
    public int Code;

    // Internal queue bookkeeping; neither field is serialized to guest siginfo_t.
    internal int? QueueOwnerUid;
    internal bool PendingWithoutInfo;
    internal bool HasFdPayload;

    // Payload (Depends on the signal type)
    // POSIX says many of these are unionized. We'll flatten them for C# ease of use.
    public int Pid;
    public uint Uid;
    public int Status;
    public long Utime;
    public long Stime;

    // sigval payload (often a ptr or an int)
    public ulong Value;

    // Fault signals (SIGSEGV/SIGBUS): siginfo_t.si_addr at offset 12.
    public uint Addr;

    // True when Addr is meaningful, including a fault at address 0. Internal
    // bookkeeping, never serialized as a separate field.
    internal bool HasAddr;

    // POSIX timers
    public int TimerId;
    public int Overrun;

    // SIGPOLL
    public long Band;
    public int Fd;
}

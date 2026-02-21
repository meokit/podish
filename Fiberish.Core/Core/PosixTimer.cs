using System;
using Fiberish.Syscalls;
using Fiberish.Native;

namespace Fiberish.Core;

public class PosixTimer
{
    public int Id { get; }
    public int ClockId { get; }
    public SigEvent SigEvent { get; }
    public Process Owner { get; }
    public Timer? ActiveTimer { get; set; }

    // Settings
    public ulong IntervalMs { get; set; }
    public ulong ValueMs { get; set; }
    public int OverrunCount { get; set; }

    public PosixTimer(int id, int clockId, SigEvent sigEvent, Process owner)
    {
        Id = id;
        ClockId = clockId;
        SigEvent = sigEvent;
        Owner = owner;
    }
}

public struct SigEvent
{
    public ulong Value; // sigevent->sigev_value
    public int Signo;   // sigevent->sigev_signo
    public int Notify;  // sigevent->sigev_notify
    public int Tid;     // sigevent->sigev_notify_thread_id
}

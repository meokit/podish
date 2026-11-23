# Scheduling and signals

## Guest slices

`KernelScheduler` runs a container's guest tasks one slice at a time. It
takes a ready `FiberTask` off the run queue and calls `RunSlice` with an
instruction budget.

At the start of a slice, `FiberTask` synchronizes its address space and
delivers pending signals if there is no syscall, completion, or continuation
in flight. It then resumes a stored continuation, finalizes a completed
syscall, or starts awaiting a pending syscall, in that order. If none of
those paths apply and the task can run, it enters the native CPU until the
budget expires or the CPU exits, for example to yield after a syscall.
[Scheduler loop](../Fiberish.Core/Core/KernelScheduler.cs) · [Task execution](../Fiberish.Core/Core/FiberTask.cs)

## Waiting syscalls

A handler returns a `ValueTask<int>`. When the handler completes during
syscall entry, `SyscallManager.Handle` can write the result to guest
`EAX`. Signal-return syscalls restore the guest registers themselves.

If a handler has to wait or returns `-ERESTARTSYS`, the syscall manager
stores the `ValueTask<int>` in `PendingSyscall`, parks the guest task,
and tells the CPU to yield. `HandleAsyncSyscallAsync` starts the await.
On completion, `RunIngress` lets the scheduler thread finalize the
result and update the guest registers. The scheduler can run other
tasks while the wait is in flight.
[Syscall entry](../Fiberish.Core/Syscalls/SyscallManager.cs) · [Syscall completion](../Fiberish.Core/Core/FiberTask.cs)

The wait families scan or harvest readiness before suspending. They return
without waiting when a descriptor is ready or the timeout is zero.
Otherwise they wait for readiness, timeout, or an interrupting signal.

| Syscalls | Shared path |
| --- | --- |
| `select`, `_newselect`, `pselect6`, `pselect6_time64` | `DoSelectWithTimeout` |
| `poll`, `ppoll`, `ppoll_time64` | `DoPoll` |
| `epoll_wait`, `epoll_pwait`, `epoll_pwait2` | `DoEpollWait` |

The timespec helpers reject negative seconds and require nanoseconds from 0
through 999,999,999. They round valid sub-millisecond remainders up. A non-null
signal-mask pointer requires the 8-byte i386 `sigset_t` size. The signal-mask
reader drops SIGKILL and SIGSTOP from the temporary mask.

The masked wait handlers save the old mask. They restore it when the wait
completes without `-ERESTARTSYS`. On an interruption, they defer restoration
so the completion path can identify the signal under the wait mask.
`sigreturn` and `rt_sigreturn` restore the deferred mask after a guest handler
runs.
[Select and poll](../Fiberish.Core/Syscalls/SyscallHandlers.Select.cs) · [Epoll](../Fiberish.Core/Syscalls/SyscallHandlers.Epoll.cs)

## Signal delivery

`PostSignalInfo` routes off-thread signals through scheduler ingress.
On the scheduler thread, it records the signal and can interrupt an
active wait if the signal is deliverable. A blocked signal stays pending.
The scheduler discards a deliverable signal with an ignore disposition.

During ordinary guest execution, `ProcessPendingSignals` checks pending
thread and process signals against the signal mask. It applies the default
action or calls `DeliverSignal` to build a guest frame and set EIP to
the handler. `SA_SIGINFO` selects the real-time frame. The legacy frame
uses `sigreturn`, and the real-time frame uses `rt_sigreturn`.
[Signal posting and frame construction](../Fiberish.Core/Core/FiberTask.cs)

An interrupted async syscall leaves a restart result for
`FinalizeAsyncSyscall`. Before a guest signal handler runs, the completion
path updates the guest registers that the frame will save. If the restart
result and the signal action allow a restart, it restores EIP to the saved
syscall instruction and reloads EAX with the syscall number. Otherwise
it writes `-EINTR` to EAX. Some waits, including `poll`, `select`, and
`epoll_wait`, return `EINTR` on interruption by a guest handler even when
that handler uses `SA_RESTART`.

When a slice sees a pending syscall, completion, or continuation, it defers
ordinary signal delivery. This lets the completion path finish updating
the guest registers before saving them in a signal frame.
[Restart and interruption logic](../Fiberish.Core/Core/FiberTask.cs) · [Wait syscall tests](../Fiberish.Tests/Syscalls/WaitSyscallTests.cs)

# Scheduling and Signal Handling Architecture

This document describes the task scheduling, asynchronous system call management, and signal handling mechanisms in the x86emu project.

## 1. Task Scheduling

The base unit of execution in the emulator is the `FiberTask`. Scheduling is managed by the `KernelScheduler`, which maintains a queue of `Ready` tasks.

### Core Loop: `RunSlice`
The primary execution entry point is `FiberTask.RunSlice(instructionLimit)`. Its responsibilities include:

1.  **Signal Pre-processing**: Calls `ProcessPendingSignals()` to check for and deliver any signals before guest execution (only if no async syscall or continuation is pending).
2.  **Continuations**: If a `Continuation` is set (from a waking awaiter), it is invoked.
3.  **Async Syscall Management**: If a `PendingSyscall` (a `Func<ValueTask<int>>`) is set, it calls `HandleAsyncSyscall()`.
4.  **Guest Execution**: If no async logic is pending, it resumes the native emulator engine (`native_run`) until a yield (interrupt, syscall, or time slice expiration).

## 2. Asynchronous System Calls

Syscalls that may block (e.g., `nanosleep`, `futex_wait`, `poll`, `read` from TTY) are implemented as C# `async` methods.

### Wait-family Syscalls (select/poll/epoll variants)
The wait-family syscalls now share common async execution paths to reduce duplication and keep signal behavior consistent:

- `select`, `_newselect`, `pselect6`, `pselect6_time64` share `DoSelectWithTimeout(...)`.
- `poll`, `ppoll`, `ppoll_time64` share `DoPoll(...)`.
- `epoll_wait`, `epoll_pwait`, `epoll_pwait2` share `DoEpollWait(...)`.

This allows one scheduling model for all readiness wait operations:
- First do a synchronous scan/harvest.
- If nothing is ready and timeout is non-zero, suspend with the corresponding awaiter.
- Resume on fd readiness, timeout, or signal interruption.

### Time and Signal Mask Handling
For `pselect6/ppoll` families, timeout and signal-mask decoding is centralized:

- `timespec` (32-bit) and `timespec64` are converted into internal millisecond timeout values.
- Invalid timespec values (negative sec/nsec or nsec >= 1e9) return `-EINVAL`.
- The temporary signal mask for `pselect6/ppoll/epoll_pwait/epoll_pwait2` is applied only around the wait call and restored in `finally`.
- `sigsetsize` must match i386 `sigset_t` size (8), otherwise `-EINVAL`.
- `SIGKILL`/`SIGSTOP` remain unmaskable (masked out from temporary masks).

### Lifecycle of an Async Syscall
1.  **Syscall Entry**: `SyscallManager.HandleSyscall` identifies the syscall and calls the handler.
2.  **Yielding**: The handler creates an `Awaiter` (e.g., `FutexAwaiter`) and returns a `ValueTask<int>`.
3.  **Task Suspension**: `HandleSyscall` (via `fiberTask.PendingSyscall`) saves the `ValueTask` and sets the task's state to `Waiting`. The task is removed from the `Ready` queue.
4.  **Background Processing**: `HandleAsyncSyscall()` (called by `RunSlice` in the next scheduler cycle) `awaits` the `ValueTask`.
5.  **Wakeup**: When the event occurs (e.g., futex wake, signal), the Awaiter's internal `TaskCompletionSource` is completed. The `KernelScheduler` moves the task back to the `Ready` queue.
6.  **Resumption**: `HandleAsyncSyscall` finishes its `await`, handles the result (including `-ERESTARTSYS`), and writes the return value to the guest's `EAX` register.

## 3. Signal Handling

Signals in x86emu are managed through a combination of `PostSignal` (enqueueing) and `ProcessPendingSignals` (delivery).

### Signal Delivery Types
There are two ways a signal is delivered to the guest:

#### A. Standard Delivery (`ProcessPendingSignals`)
Occurs at the start of `RunSlice` if the guest is about to run normal instructions.
- Checks `PendingSignals` against `SignalMask`.
- If a signal is found and not ignored, it calls `DeliverSignal(...)`.
- `DeliverSignal` builds a signal frame on the guest stack (using `SetupOldSigFrame` or `SetupSigContext`) and redirects `EIP` to the signal handler.

#### B. Interrupting Delivery (`HandleAsyncSyscall`)
Occurs when an async syscall is interrupted (returns `-ERESTARTSYS`).
- If a signal arrives while a task is `Waiting`, `PostSignal` sets `WakeReason.Signal`.
- The Awaiter (e.g., `FutexAwaiter`, `SelectAwaiter`, `PollAwaiter`, epoll wait) detects the signal, returns `Interrupted`, and the syscall handler returns `-ERESTARTSYS`.
- `HandleAsyncSyscall` detects `-ERESTARTSYS` and calls `DeliverSignalForRestart(...)`.
- **Crucial**: This path adjusts the guest registers (EIP rewound or EAX set to `-EINTR`) *before* building the signal frame, ensuring the state restored after `sigreturn` is the correct "post-syscall" state.

### RunSlice Guard
To prevent a signal from being delivered twice (and creating nested `sigreturn` frames with stale registers), `RunSlice` contains a guard:
```csharp
if (PendingSyscall == null && Continuation == null)
{
    ProcessPendingSignals();
}
```
This ensures that if a syscall is currently in-flight, signal handling is deferred to `HandleAsyncSyscall`, which can coordinate the signal delivery with the syscall's return state.

## 4. Register Management during Signals

- **Syscall Restart**: If a syscall returns `-ERESTARTSYS` and `SA_RESTART` is set, `HandleAsyncSyscall` rewinds `EIP` to the `int 0x80` instruction before delivering the signal. After `sigreturn`, the syscall re-executes.
- **Interruption**: If `SA_RESTART` is NOT set, `HandleAsyncSyscall` sets `EAX = -EINTR` before delivery. After `sigreturn`, userspace receives `EINTR`.
- **Frame Layouts**:
    - **Old Sigframe**: Used when `SA_SIGINFO` is not set. Compatible with musl's `sigreturn` (vDSO or `__restore`).
    - **RT Sigframe**: Used when `SA_SIGINFO` is set. Required for handlers that expect `siginfo_t` and `ucontext_t`.

## 5. Current Wait-syscall Coverage

Implemented wait syscalls relevant to scheduling and signal interaction:

- `select`, `_newselect`, `pselect6`, `pselect6_time64`
- `poll`, `ppoll`, `ppoll_time64`
- `epoll_create`, `epoll_create1`, `epoll_ctl`, `epoll_wait`, `epoll_pwait`, `epoll_pwait2`

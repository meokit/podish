using Microsoft.Extensions.Logging;

namespace Fiberish.Core;

internal static partial class FiberTaskLog
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "[Interrupt] vector=0x{Vector:X}")]
    internal static partial void HandleInterruptVector(ILogger logger, uint vector);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[Interrupt] fault vector=0x{Vector:X}; yielding to scheduler")]
    internal static partial void HandleInterruptFaultYield(ILogger logger, uint vector);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[RunSlice] PendingSyscall TID={Tid} handlingAsync={HandlingAsyncSyscall} status={Status} mode={ExecutionMode}")]
    internal static partial void RunSlicePendingSyscall(ILogger logger, int tid, bool handlingAsyncSyscall,
        FiberTaskStatus status, TaskExecutionMode executionMode);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[RunSlice] Starting HandleAsyncSyscall phase-2 for TID={Tid}")]
    internal static partial void RunSliceStartHandleAsyncPhase2(ILogger logger, int tid);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[RunSlice] Skipping HandleAsyncSyscall phase-2 for TID={Tid}; already active")]
    internal static partial void RunSliceSkipHandleAsyncPhase2(ILogger logger, int tid);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[RunSlice] Yielded with PendingSyscall TID={Tid} handlingAsync={HandlingAsyncSyscall}")]
    internal static partial void RunSliceYieldedWithPendingSyscall(ILogger logger, int tid,
        bool handlingAsyncSyscall);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[RunSlice] Starting HandleAsyncSyscall from yield for TID={Tid}")]
    internal static partial void RunSliceStartHandleAsyncYield(ILogger logger, int tid);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[RunSlice] Skipping HandleAsyncSyscall from yield for TID={Tid}; already active")]
    internal static partial void RunSliceSkipHandleAsyncYield(ILogger logger, int tid);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[HandleAsyncSyscall] Re-entry suppressed")]
    internal static partial void HandleAsyncSyscallReentry(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[HandleAsyncSyscall] Unhandled exception TID={Tid} hasPending={HasPendingSyscall} ret={Ret}")]
    internal static partial void HandleAsyncSyscallUnhandled(ILogger logger, Exception exception, int tid,
        bool hasPendingSyscall, int ret);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[HandleAsyncSyscall] Scheduling TID={Tid} currentThread={CurrentThreadId} ownerThread={OwnerThreadId} status={Status} readyQueued={IsReadyQueued}")]
    internal static partial void HandleAsyncSyscallFinalizeScheduling(ILogger logger, int tid, int currentThreadId,
        int ownerThreadId, FiberTaskStatus status, bool isReadyQueued);

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "[HandleAsyncSyscall] Leave TID={Tid} hasPending={HasPendingSyscall} status={Status}")]
    internal static partial void HandleAsyncSyscallLeave(ILogger logger, int tid, bool hasPendingSyscall,
        FiberTaskStatus status);
}
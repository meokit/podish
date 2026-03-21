namespace Fiberish.Core;

internal enum SchedulerWorkItemKind
{
    ResumeTask,
    RunAction,
    WakeScheduler,
    DispatchSyncContextPost,
    DispatchSyncContextSend,
    SetWaitReasonAndRunContinuation,
    FinalizeAsyncSyscall,
    FinalizeTaskRetirement
}

internal enum WaitContinuationMode
{
    RunAction,
    ResumeTask
}

internal readonly struct SchedulerWorkItem
{
    private SchedulerWorkItem(
        SchedulerWorkItemKind kind,
        FiberTask? context = null,
        FiberTask? task = null,
        Action? action = null,
        SendOrPostCallback? callback = null,
        object? state = null,
        SyncContextSendRequest? sendRequest = null,
        FiberTask.WaitToken waitToken = default,
        WakeReason waitReason = WakeReason.None,
        WaitContinuationMode continuationMode = WaitContinuationMode.RunAction,
        int asyncResult = 0)
    {
        Kind = kind;
        Context = context;
        Task = task;
        Action = action;
        Callback = callback;
        State = state;
        SendRequest = sendRequest;
        WaitToken = waitToken;
        WaitReason = waitReason;
        ContinuationMode = continuationMode;
        AsyncResult = asyncResult;
    }

    public SchedulerWorkItemKind Kind { get; }
    public FiberTask? Context { get; }
    public FiberTask? Task { get; }
    public Action? Action { get; }
    public SendOrPostCallback? Callback { get; }
    public object? State { get; }
    public SyncContextSendRequest? SendRequest { get; }
    public FiberTask.WaitToken WaitToken { get; }
    public WakeReason WaitReason { get; }
    public WaitContinuationMode ContinuationMode { get; }
    public int AsyncResult { get; }

    public static SchedulerWorkItem ResumeTask(FiberTask task)
    {
        return new SchedulerWorkItem(SchedulerWorkItemKind.ResumeTask, task, task);
    }

    public static SchedulerWorkItem RunAction(Action action, FiberTask? context = null)
    {
        return new SchedulerWorkItem(SchedulerWorkItemKind.RunAction, context, action: action);
    }

    public static SchedulerWorkItem WakeScheduler()
    {
        return new SchedulerWorkItem(SchedulerWorkItemKind.WakeScheduler);
    }

    public static SchedulerWorkItem DispatchSyncContextPost(
        FiberTask? context,
        SendOrPostCallback callback,
        object? state)
    {
        return new SchedulerWorkItem(
            SchedulerWorkItemKind.DispatchSyncContextPost,
            context,
            callback: callback,
            state: state);
    }

    public static SchedulerWorkItem DispatchSyncContextSend(
        FiberTask? context,
        SendOrPostCallback callback,
        object? state,
        SyncContextSendRequest request)
    {
        return new SchedulerWorkItem(
            SchedulerWorkItemKind.DispatchSyncContextSend,
            context,
            callback: callback,
            state: state,
            sendRequest: request);
    }

    public static SchedulerWorkItem SetWaitReasonAndRunContinuation(
        FiberTask task,
        FiberTask.WaitToken waitToken,
        WakeReason reason,
        Action continuation,
        WaitContinuationMode continuationMode)
    {
        return new SchedulerWorkItem(
            SchedulerWorkItemKind.SetWaitReasonAndRunContinuation,
            task,
            task,
            continuation,
            waitToken: waitToken,
            waitReason: reason,
            continuationMode: continuationMode);
    }

    public static SchedulerWorkItem FinalizeAsyncSyscall(FiberTask task, int result, Exception? error)
    {
        return new SchedulerWorkItem(
            SchedulerWorkItemKind.FinalizeAsyncSyscall,
            task,
            task,
            state: error,
            asyncResult: result);
    }

    public static SchedulerWorkItem FinalizeTaskRetirement(FiberTask task)
    {
        return new SchedulerWorkItem(
            SchedulerWorkItemKind.FinalizeTaskRetirement,
            task,
            task);
    }
}

internal sealed class SyncContextSendRequest : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(false);

    public Exception? Thrown { get; set; }

    public void Dispose()
    {
        _gate.Dispose();
    }

    public void SetCompleted()
    {
        _gate.Set();
    }

    public void Wait()
    {
        _gate.Wait();
    }
}
namespace Fiberish.Core;

internal enum SchedulerWorkItemKind
{
    ResumeTask,
    IngressAction,
    WakeScheduler,
    DispatchSyncContextPost,
    DispatchSyncContextSend,
    FinalizeTaskRetirement
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
        FiberTask.WaitToken waitToken = default)
    {
        Kind = kind;
        Context = context;
        Task = task;
        Action = action;
        Callback = callback;
        State = state;
        SendRequest = sendRequest;
        WaitToken = waitToken;
    }

    public SchedulerWorkItemKind Kind { get; }
    public FiberTask? Context { get; }
    public FiberTask? Task { get; }
    public Action? Action { get; }
    public SendOrPostCallback? Callback { get; }
    public object? State { get; }
    public SyncContextSendRequest? SendRequest { get; }
    public FiberTask.WaitToken WaitToken { get; }

    public static SchedulerWorkItem ResumeTask(FiberTask task)
    {
        return new SchedulerWorkItem(SchedulerWorkItemKind.ResumeTask, task, task);
    }

    public static SchedulerWorkItem IngressAction(Action action, FiberTask? context = null)
    {
        return new SchedulerWorkItem(SchedulerWorkItemKind.IngressAction, context, action: action);
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
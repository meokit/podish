using System;
using System.Collections.Generic;

namespace Bifrost.Core;

/// <summary>
/// A lightweight, single-threaded promise for waiting on events (Process exit, etc.)
/// </summary>
public class WaitHandle
{
    private readonly List<Action> _continuations = new();
    public bool IsSet { get; private set; }

    public void Set()
    {
        if (IsSet) return;
        IsSet = true;
        foreach (var continuation in _continuations)
        {
            continuation();
        }
        _continuations.Clear();
    }

    public void Reset()
    {
        IsSet = false;
        _continuations.Clear();
    }

    public void Register(Action continuation)
    {
        if (IsSet)
        {
            continuation();
        }
        else
        {
            _continuations.Add(continuation);
        }
    }
    
    public WaitHandleAwaiter GetAwaiter() => new(this);
}

public struct WaitHandleAwaiter : System.Runtime.CompilerServices.INotifyCompletion
{
    private readonly WaitHandle _handle;
    public WaitHandleAwaiter(WaitHandle handle) => _handle = handle;
    public bool IsCompleted => _handle.IsSet;
    public void OnCompleted(Action continuation) => _handle.Register(continuation);
    public void GetResult() { }
}

namespace Fiberish.Core.Utils;

/// <summary>
///     A Rust-style Mutex wrapper that encapsulates a value and ensures it can only be accessed
///     while explicitly holding the lock via closures.
/// </summary>
public class Locked<T>
{
    private readonly Lock _lock = new();
    private readonly T _value;

    public Locked(T value)
    {
        _value = value;
    }

    /// <summary>
    ///     Executes the given action while holding the lock on the encapsulated value.
    /// </summary>
    public void Lock(Action<T> action)
    {
        lock (_lock)
        {
            action(_value);
        }
    }

    /// <summary>
    ///     Executes the given function while holding the lock on the encapsulated value,
    ///     and returns the result.
    /// </summary>
    public TResult Lock<TResult>(Func<T, TResult> func)
    {
        lock (_lock)
        {
            return func(_value);
        }
    }
}
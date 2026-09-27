namespace RadSharp.Threading;

/// <summary>
/// A SynchronizationContext that queues continuations onto a UI thread callback.
/// Used to make `await` statements in event handlers resume on the UI thread.
/// </summary>
internal sealed class UiSynchronizationContext : SynchronizationContext
{
    private readonly Action<Action> _post;

    public UiSynchronizationContext(Action<Action> post)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        _post(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        // For Send, we'd need to block and wait for completion.
        // For now, we'll just delegate to Post (not ideal, but avoids complexity).
        // Callers should use Post or await instead of Send.
        _post(() => d(state));
    }
}

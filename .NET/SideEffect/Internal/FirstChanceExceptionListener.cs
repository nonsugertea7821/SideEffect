using System.Runtime.ExceptionServices;

namespace SideEffect.Internal;

/// <summary>
/// Observes CLR FirstChanceException events and converts SideEffectException
/// instances into asynchronous SideEffect events.
///
/// FirstChanceException is intentionally used instead of catch-based handling.
///
/// A catch-based approach requires every application layer to explicitly invoke
/// notification logic or propagate exceptions through a shared boundary.
///
/// By observing exceptions at the runtime level, SideEffect keeps business code
/// unaware of cross-cutting concerns.
///
/// This listener only performs observation and event creation.
/// Actual processing is delegated to Worker.
/// </summary>
internal sealed class FirstChanceExceptionListener : IDisposable
{
    private readonly Worker _worker;
    private int _disposed;

    public FirstChanceExceptionListener(Worker worker)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    /// <summary>
    /// Receives CLR exception notifications and determines whether the exception
    /// represents a SideEffect event.
    ///
    /// The listener intentionally performs only lightweight operations:
    ///
    /// - Lifecycle state validation.
    /// - Re-entry prevention check.
    /// - Exception type filtering.
    /// - Duplicate observation prevention.
    /// - Event enqueueing.
    ///
    /// Handler execution is never performed in this callback because the callback
    /// executes on the original exception path.
    /// </summary>
    private void OnFirstChanceException(
        object? sender,
        FirstChanceExceptionEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) { return; }

        // Ignore exceptions generated inside SideEffect itself.
        //
        // Without this boundary, handlers or internal processing could create
        // recursive event chains.
        if (ReEntryPreventer.IsSuppressed) { return; }

        // Only SideEffectException carries semantic meaning for this pipeline.
        //
        // Ordinary exceptions remain handled by normal application behavior.
        if (e.Exception is not SideEffectException exception) { return; }

        // CLR may notify the same exception instance multiple times when it is
        // re-thrown.
        //
        // SideEffect treats an exception instance as one semantic event, so
        // duplicate observation must be prevented.
        if (!MultipleObservationPreventer.TryMarkObserved(exception)) { return; }

        // The event is handed off immediately.
        //
        // Actual side effect execution occurs asynchronously in Worker.
        _worker.TryEnqueue(exception);
    }

    /// <summary>
    /// Stops observing CLR exception notifications.
    ///
    /// Unsubscribing is required to release the listener lifecycle and avoid
    /// retaining references after SideEffect shutdown.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
    }

}
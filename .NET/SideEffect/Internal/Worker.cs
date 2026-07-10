namespace SideEffect.Internal;

/// <summary>
/// Executes queued SideEffect events asynchronously.
///
/// Worker provides execution isolation from the original exception source.
///
/// The worker guarantees that:
///
/// - Business execution is not blocked by side effect processing.
/// - Handler failures do not terminate the processing loop.
/// - Shutdown can complete without leaving uncontrolled background tasks.
///
/// WorkerCount controls the number of independent processing loops.
/// </summary>
internal sealed class Worker : IAsyncDisposable
{
    private readonly Queue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private int _disposed;

    public Worker(Queue queue, Dispatcher dispatcher, int workerCount)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));

        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        if (workerCount < 1) { workerCount = 1; }

        _workers =
        [
            .. Enumerable
                .Range(0, workerCount)
                .Select(_ => Task.Run(RunAsync))
        ];
    }

    /// <summary>
    /// Converts an exception into a queued SideEffect event.
    ///
    /// This method is called from FirstChanceException processing.
    /// It must remain lightweight because it executes on the application's
    /// exception path.
    /// </summary>
    public bool TryEnqueue(SideEffectException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return _queue.TryPublish(
            new SideEffectEvent(
                exception,
                DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Continuously consumes events and executes dispatch processing.
    ///
    /// Any exception produced inside the SideEffect pipeline is isolated.
    /// The worker must remain alive even when individual processing fails.
    /// </summary>
    private async Task RunAsync()
    {
        try
        {
            await foreach (
                var @event in _queue
                    .ReadAllAsync(_cts.Token)
                    .ConfigureAwait(false))
            {
                try
                {
                    // Prevent exceptions generated during SideEffect handling
                    // from being interpreted as new external SideEffect events.
                    using var _ = ReEntryPreventer.Suppress();

                    await _dispatcher
                        .DispatchAsync(
                            @event,
                            _cts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // SideEffect pipeline failures are isolated.
                    // The worker continues processing subsequent events because
                    // one failed side effect must not disable the entire system.
                }
            }
        }
        catch (OperationCanceledException)
            when (_cts.IsCancellationRequested)
        {
            // Cancellation is an expected shutdown condition.
        }
    }

    /// <summary>
    /// Stops event processing and releases worker resources.
    ///
    /// Shutdown is best-effort. SideEffect must not throw exceptions during
    /// disposal because it is an infrastructure component.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }

        _queue.Complete();

        _cts.Cancel();

        try
        {
            await Task
                .WhenAll(_workers)
                .ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown failures.
            // Disposal must complete even when background processing failed.
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
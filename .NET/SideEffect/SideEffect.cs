using System.Threading.Channels;
using SideEffect.Internal;

namespace SideEffect;

/// <summary>
/// Provides the runtime environment for SideEffect processing.
///
/// SideEffect separates cross-cutting reactions from business logic.
/// Application code does not explicitly invoke notifications, logging,
/// auditing, or recovery handlers.
///
/// Instead, SideEffect observes SideEffectException instances and executes
/// registered reactions asynchronously outside the original execution path.
///
/// The processing flow is:
///
/// Exception occurrence
///     -> FirstChanceException observation
///     -> Duplicate observation prevention
///     -> Asynchronous queueing
///     -> Handler dispatch
///
/// The responsibility of this class is lifecycle management of the entire
/// SideEffect pipeline.
/// </summary>
public sealed class SideEffect : IAsyncDisposable, IDisposable
{
    private readonly Registry _registry;
    private readonly Queue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly Worker _worker;
    private readonly FirstChanceExceptionListener _listener;
    private int _disposed;

    public SideEffect(Func<Type, object?> handlerResolver, SideEffectOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handlerResolver);

        options ??= new SideEffectOptions();

        _registry = new Registry();
        _queue = new Queue(options);
        _dispatcher = new Dispatcher(_registry, handlerResolver);
        _worker = new Worker(_queue, _dispatcher, options.WorkerCount);
        _listener = new FirstChanceExceptionListener(_worker);
    }

    public ISideEffectRegistry Registry => _registry;

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }

        _listener.Dispose();

        await _worker.DisposeAsync().ConfigureAwait(false);
    }

}

/// <summary>
/// Represents an exception that carries semantic meaning for SideEffect processing.
///
/// Unlike ordinary exceptions, SideEffectException is not only an error signal.
/// It represents an observable event that may trigger external reactions such as
/// notifications, auditing, metrics collection, or compensating actions.
///
/// The exception instance itself acts as the identity of the observed event.
/// </summary>
public abstract class SideEffectException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>
    /// Unique identity of this exception event.
    ///
    /// This identifier allows external systems to correlate side effects
    /// generated from the same semantic occurrence.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Timestamp when this exception event object was created.
    /// </summary>
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Determines whether a SideEffect handler should receive a specific exception.
///
/// Filters separate event occurrence from reaction conditions.
/// The same exception type may represent multiple situations, and filters allow
/// handlers to subscribe only to meaningful cases.
///
/// Implementations must be thread-safe because filtering can occur concurrently.
/// </summary>
public interface ISideEffectFilter
{
    bool Match(SideEffectException exception);
}

/// <summary>
/// Defines a reaction executed after a SideEffectException is observed.
///
/// Handlers execute outside the original business execution path.
/// Therefore, handler failures must not affect the original exception flow.
///
/// Implementations must be thread-safe because multiple SideEffect events may
/// be processed concurrently.
///
/// Implementations must be idempotent because external side effects may require
/// retry, recovery, or duplicate prevention strategies.
/// </summary>
public interface ISideEffectHandler<in TException> where TException : SideEffectException
{
    ValueTask HandleAsync(TException exception, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides registration capabilities for SideEffect handlers.
///
/// Registration defines which reactions exist.
/// Dispatch determines when those reactions are executed.
///
/// Registration is expected to happen infrequently, while exception observation
/// and dispatch may happen frequently.
///
/// Implementations must allow concurrent reads during dispatch.
/// </summary>
public interface ISideEffectRegistry
{
    IDisposable Register<TException, THandler>(
        ISideEffectFilter filter)
        where TException : SideEffectException
        where THandler : class, ISideEffectHandler<TException>;
}

/// <summary>
/// Configuration options for SideEffect execution.
///
/// QueueCapacity less than or equal to zero creates an unbounded queue.
///
/// WorkerCount controls the number of asynchronous workers.
/// A value of one provides stronger ordering guarantees because events are
/// processed sequentially.
///
/// The configuration represents the trade-off between throughput,
/// memory consumption, and back-pressure behavior.
/// </summary>
public sealed class SideEffectOptions
{
    public int QueueCapacity { get; init; } = 0;

    public int WorkerCount { get; init; } = 1;

    public BoundedChannelFullMode FullMode { get; init; }
        = BoundedChannelFullMode.Wait;
}
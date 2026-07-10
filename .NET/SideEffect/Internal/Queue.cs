using System.Threading.Channels;

namespace SideEffect.Internal;

/// <summary>
/// Provides asynchronous buffering between exception observation and handler execution.
///
/// Exception observation happens on the throwing thread and must remain lightweight.
/// Therefore, SideEffect converts observed exceptions into events and places them
/// into this queue instead of executing handlers immediately.
///
/// The queue provides isolation between:
///
/// - The application execution path.
/// - The SideEffect processing pipeline.
///
/// Queue configuration controls the balance between memory consumption,
/// throughput, and back-pressure behavior.
/// </summary>
internal sealed class Queue
{
    private readonly Channel<SideEffectEvent> _channel;

    public Queue(SideEffectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var singleReader = options.WorkerCount <= 1;

        if (options.QueueCapacity <= 0)
        {
            _channel = Channel.CreateUnbounded<SideEffectEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = singleReader,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
        }
        else
        {
            _channel = Channel.CreateBounded<SideEffectEvent>(
                new BoundedChannelOptions(options.QueueCapacity)
                {
                    SingleReader = singleReader,
                    SingleWriter = false,
                    FullMode = options.FullMode,
                    AllowSynchronousContinuations = false
                });
        }
    }

    /// <summary>
    /// Attempts to enqueue an event without blocking the caller.
    ///
    /// The caller is normally the FirstChanceException callback.
    /// Blocking this operation would alter application execution behavior,
    /// therefore enqueueing must remain lightweight.
    /// </summary>
    public bool TryPublish(SideEffectEvent @event)
        => _channel.Writer.TryWrite(@event);

    /// <summary>
    /// Reads queued events asynchronously until completion or cancellation.
    ///
    /// Workers consume events independently from the original exception source.
    /// </summary>
    public IAsyncEnumerable<SideEffectEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Completes the queue and prevents new events from being published.
    ///
    /// Existing consumers are allowed to finish their current processing.
    /// </summary>
    public void Complete() 
        => _channel.Writer.TryComplete();
}

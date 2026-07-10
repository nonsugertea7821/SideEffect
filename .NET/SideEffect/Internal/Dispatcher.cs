namespace SideEffect.Internal;

/// <summary>
/// Resolves applicable handlers and executes side effects.
///
/// Dispatcher only performs handler selection and invocation.
/// It does not observe exceptions, manage queues, or control lifecycle.
///
/// This separation keeps the SideEffect pipeline composed of independent
/// responsibilities:
///
/// Observation -> Queueing -> Dispatch -> Handling
///
/// Handler failures are isolated because SideEffect is an auxiliary execution
/// path and must never become a failure source for the application.
/// </summary>
internal sealed class Dispatcher(Registry registry, Func<Type, object?> resolver)
{
    private readonly Registry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    private readonly Func<Type, object?> _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>
    /// Dispatches an observed event to all matching handlers.
    ///
    /// Multiple handlers may receive the same exception event.
    /// Each handler execution is isolated so that one failure does not prevent
    /// other registered handlers from running.
    /// </summary>
    public async ValueTask DispatchAsync(SideEffectEvent @event, CancellationToken cancellationToken)
    {
        var registrations = _registry.Find(@event.Exception);

        foreach (var registration in registrations)
        {
            try
            {
                var invoker = registration.CreateInvoker(_resolver);

                await invoker
                    .HandleAsync(@event.Exception,cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // SideEffect is an auxiliary execution pipeline.
                //
                // A failure inside SideEffect must never affect the original
                // application execution contract.
                //
                // Handler failures are intentionally isolated so processing
                // can continue with remaining handlers.
            }
        }
    }
}

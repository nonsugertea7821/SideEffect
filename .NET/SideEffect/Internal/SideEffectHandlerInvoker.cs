namespace SideEffect.Internal;

/// <summary>
/// Represents the relationship between an exception semantic type and its handler.
///
/// A registration is immutable after creation.
/// The registry maintains these registrations as immutable snapshots so that
/// dispatch can execute without locking.
///
/// </summary>
internal sealed record HandlerRegistration(
    Type ExceptionType,
    Type HandlerType,
    ISideEffectFilter Filter,
    Func<Func<Type, object?>, ISideEffectHandlerInvoker> InvokerFactory)
{
    /// <summary>
    /// Creates a handler invoker using the provided resolver.
    /// </summary>
    /// <param name="resolver">A function that resolves a handler type to an instance.</param>
    /// <returns>An invoker that can execute the handler for the associated exception type.</returns>
    public ISideEffectHandlerInvoker CreateInvoker(Func<Type, object?> resolver) => InvokerFactory(resolver);
}

internal interface ISideEffectHandlerInvoker
{
    ValueTask HandleAsync(SideEffectException exception, CancellationToken cancellationToken);
}

internal sealed class SideEffectHandlerInvoker<TException, THandler>(THandler handler): ISideEffectHandlerInvoker
    where TException : SideEffectException
    where THandler : class, ISideEffectHandler<TException>
{
    private readonly THandler _handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    public ValueTask HandleAsync(SideEffectException exception, CancellationToken cancellationToken)
    {
        if (exception is not TException typed)
        {
            return ValueTask.CompletedTask;
        }

        return _handler.HandleAsync(typed, cancellationToken);
    }
}
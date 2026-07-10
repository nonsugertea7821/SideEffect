using System.Collections.Immutable;

namespace SideEffect.Internal;

/// <summary>
/// Stores and resolves SideEffect handler registrations.
///
/// The registry is optimized for frequent concurrent reads and infrequent
/// modifications.
///
/// Immutable snapshots allow dispatch operations to continue safely while
/// registrations are added or removed.
/// </summary>
internal sealed class Registry : ISideEffectRegistry
{
    private ImmutableArray<HandlerRegistration> _registrations = ImmutableArray<HandlerRegistration>.Empty;

    public IDisposable Register<TException, THandler>(ISideEffectFilter filter)
        where TException : SideEffectException
        where THandler : class, ISideEffectHandler<TException>
    {
        ArgumentNullException.ThrowIfNull(filter);

        var registration = new HandlerRegistration(
            typeof(TException),
            typeof(THandler),
            filter,
            resolver =>
            {
                var instance = resolver(typeof(THandler));

                if (instance is null)
                {
                    throw new InvalidOperationException(
                        $"Handler type '{typeof(THandler).FullName}' could not be resolved.");
                }

                if (instance is not THandler handler)
                {
                    throw new InvalidOperationException(
                        $"Resolved instance is not '{typeof(THandler).FullName}'.");
                }

                return new SideEffectHandlerInvoker<TException, THandler>(
                    handler);
            });

        ImmutableInterlocked.Update(ref _registrations, current => current.Add(registration));

        return new DisposableAction(() =>
        {
            ImmutableInterlocked.Update(
                ref _registrations,
                current => current.Remove(registration));
        });
    }

    internal ImmutableArray<HandlerRegistration> Find(SideEffectException exception)
    {
        var snapshot = _registrations;
        return [..snapshot.Where(x =>
                    x.ExceptionType.IsAssignableFrom(exception.GetType()) &&
                    x.Filter.Match(exception))];
    }

}

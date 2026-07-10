using System.Runtime.CompilerServices;

namespace SideEffect.Internal;

/// <summary>
/// Ensures that the same exception instance is observed only once.
///
/// CLR FirstChanceException is raised every time an exception is thrown,
/// including when the same exception instance is re-thrown.
///
/// Without this protection, a single semantic event could trigger duplicate
/// side effects.
///
/// ConditionalWeakTable is used so that the tracking state does not prevent
/// garbage collection of exception instances after they are no longer used.
/// </summary>
internal static class MultipleObservationPreventer
{
    private sealed class ObservationState{ public int Seen; }

    private static readonly ConditionalWeakTable<Exception, ObservationState> States = new();

    public static bool TryMarkObserved(Exception exception)
    {
        var state = States.GetValue(exception, _ => new ObservationState());
        return Interlocked.Exchange(ref state.Seen, 1) == 0;
    }
}

/// <summary>
/// Prevents SideEffect infrastructure execution from being interpreted as a
/// new external event.
///
/// SideEffect handlers execute arbitrary application code.
/// That code may throw SideEffectException again.
///
/// Such exceptions belong to the internal processing flow and must not create
/// recursive SideEffect execution chains.
///
/// This mechanism defines the boundary between observed application events and
/// SideEffect infrastructure behavior.
/// </summary>
internal static class ReEntryPreventer
{
    private static readonly AsyncLocal<int> SuppressionDepth = new();

    public static IDisposable Suppress()
    {
        SuppressionDepth.Value++;

        return new DisposableAction(() =>
        {
            var current = SuppressionDepth.Value;

            if (current > 0)
            {
                SuppressionDepth.Value = current - 1;
            }
        });
    }

    public static bool IsSuppressed => SuppressionDepth.Value > 0;
}

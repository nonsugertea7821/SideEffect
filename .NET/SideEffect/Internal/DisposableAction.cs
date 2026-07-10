namespace SideEffect.Internal;

internal sealed class DisposableAction(Action dispose) : IDisposable
{
    private Action? _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
    public void Dispose()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
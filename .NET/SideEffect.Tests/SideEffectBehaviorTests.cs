using System.Collections.Concurrent;
using Xunit;

namespace SideEffect.Tests;

public sealed class SideEffectBehaviorTests
{
    [Fact]
    public async Task CatchLocally_StillExecutesHandlerAsynchronously()
    {
        var notifier = new RecordingHandler();
        await using var sut = CreateSideEffect(notifier, filter: new MatchAllFilter());

        var recovered = false;

        try
        {
            throw new SampleSideEffectException("business failure");
        }
        catch (SampleSideEffectException)
        {
            recovered = true;
        }

        Assert.True(recovered);

        var observed = await notifier.WaitForCountAsync(1, TimeSpan.FromSeconds(3));
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task SameExceptionInstanceRethrown_IsProcessedOnlyOnce()
    {
        var notifier = new RecordingHandler();
        await using var sut = CreateSideEffect(notifier, filter: new MatchAllFilter());

        var shared = new SampleSideEffectException("duplicate");

        try
        {
            throw shared;
        }
        catch (SampleSideEffectException)
        {
            // Simulate retry path that rethrows the same instance.
        }

        try
        {
            throw shared;
        }
        catch (SampleSideEffectException)
        {
            // Ignored for recovery.
        }

        var observed = await notifier.WaitForCountAsync(1, TimeSpan.FromSeconds(3));
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task NonMatchingFilter_DoesNotDispatchToHandler()
    {
        var notifier = new RecordingHandler();
        await using var sut = CreateSideEffect(notifier, filter: new NeverMatchFilter());

        try
        {
            throw new SampleSideEffectException("filtered out");
        }
        catch (SampleSideEffectException)
        {
            // Expected local recovery.
        }

        var observed = await notifier.WaitForCountAsync(1, TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, observed);
    }

    [Fact]
    public async Task FailingHandler_DoesNotBlockOtherHandlers()
    {
        var failing = new ThrowingHandler();
        var recorder = new RecordingHandler();

        await using var sut = CreateSideEffect(
            resolver: type =>
            {
                if (type == typeof(ThrowingHandler)) { return failing; }
                if (type == typeof(RecordingHandler)) { return recorder; }
                return null;
            },
            register: registry =>
            {
                registry.Register<SampleSideEffectException, ThrowingHandler>(new MatchAllFilter());
                registry.Register<SampleSideEffectException, RecordingHandler>(new MatchAllFilter());
            });

        try
        {
            throw new SampleSideEffectException("isolation");
        }
        catch (SampleSideEffectException)
        {
            // Keep business flow alive.
        }

        var observed = await recorder.WaitForCountAsync(1, TimeSpan.FromSeconds(3));
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task AfterDispose_NewExceptionsAreNotObserved()
    {
        var notifier = new RecordingHandler();
        var sut = CreateSideEffect(notifier, filter: new MatchAllFilter());

        await sut.DisposeAsync();

        try
        {
            throw new SampleSideEffectException("after dispose");
        }
        catch (SampleSideEffectException)
        {
            // Application keeps handling as usual.
        }

        var observed = await notifier.WaitForCountAsync(1, TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, observed);
    }

    [Fact]
    public void Constructor_ThrowsWhenResolverIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new global::SideEffect.SideEffect(null!));
    }

    private static global::SideEffect.SideEffect CreateSideEffect(
        object handler,
        ISideEffectFilter filter)
    {
        return CreateSideEffect(
            resolver: type => type == handler.GetType() ? handler : null,
            register: registry =>
            {
                registry.Register<SampleSideEffectException, RecordingHandler>(filter);
            });
    }

    private static global::SideEffect.SideEffect CreateSideEffect(
        Func<Type, object?> resolver,
        Action<ISideEffectRegistry> register)
    {
        var sideEffect = new global::SideEffect.SideEffect(
            resolver,
            new SideEffectOptions
            {
                WorkerCount = 1
            });

        register(sideEffect.Registry);

        return sideEffect;
    }

    private sealed class SampleSideEffectException(string message)
        : SideEffectException(message);

    private sealed class MatchAllFilter : ISideEffectFilter
    {
        public bool Match(SideEffectException exception) => true;
    }

    private sealed class NeverMatchFilter : ISideEffectFilter
    {
        public bool Match(SideEffectException exception) => false;
    }

    private sealed class RecordingHandler : ISideEffectHandler<SampleSideEffectException>
    {
        private int _count;
        private readonly ConcurrentQueue<SampleSideEffectException> _events = new();
        private readonly TaskCompletionSource _firstEvent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask HandleAsync(
            SampleSideEffectException exception,
            CancellationToken cancellationToken = default)
        {
            _events.Enqueue(exception);
            Interlocked.Increment(ref _count);
            _firstEvent.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task<int> WaitForCountAsync(int expected, TimeSpan timeout)
        {
            if (expected <= 0)
            {
                return Volatile.Read(ref _count);
            }

            using var cts = new CancellationTokenSource(timeout);

            while (Volatile.Read(ref _count) < expected && !cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            return _count;
        }
    }

    private sealed class ThrowingHandler : ISideEffectHandler<SampleSideEffectException>
    {
        public ValueTask HandleAsync(
            SampleSideEffectException exception,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("handler failure");
        }
    }
}

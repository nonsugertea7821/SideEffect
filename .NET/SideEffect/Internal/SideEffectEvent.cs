namespace SideEffect.Internal;

/// <summary>
/// Represents an observed SideEffect event waiting for asynchronous processing.
///
/// The event separates exception observation from execution.
/// Exception capture occurs synchronously during FirstChanceException handling,
/// while actual side effect execution is performed later by Worker.
///
/// This separation prevents side effects from affecting the original execution
/// path of the application.
/// </summary>
internal sealed record SideEffectEvent(SideEffectException Exception, DateTimeOffset EnqueuedAt);
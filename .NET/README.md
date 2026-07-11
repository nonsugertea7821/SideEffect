# SideEffect API & Architecture Note

This note describes the public API surface of SideEffect, its architectural philosophy, and operational guidelines.

## Core Architectural Philosophy

SideEffect is designed to **completely decouple peripheral asynchronous processing (side effects) from the primary thread of business logic**.
By utilizing standard C# exceptions (`throw`) as signals for domain events, it provides a balance between developer experience and separation of concerns.

---

## 1. Runtime API Specification

### 1.1 Core Runtime

#### `SideEffect`

* **Type**: `sealed class`
* **Lifetime**: Created as a singleton at application startup, maintained as a single instance, and disposed of on application shutdown.
* **Constructor**: `SideEffect(Func<Type, object?> handlerResolver, SideEffectOptions? options = null)`
* `handlerResolver`: A delegate to resolve handler instances by type from a DI container or a factory.
* `options`: Optional runtime configurations for queue capacity and concurrent worker count. Defaults are used when omitted.


* **Members**:
* `ISideEffectRegistry Registry { get; }` (Registry for handler mapping)
* `void Dispose()` / `ValueTask DisposeAsync()` (Detaches global observation and processes remaining in-flight queue items before final shutdown)



### 1.2 Event Exception Model

#### `SideEffectException`

* **Type**: `abstract class`, inherits `System.Exception`
* **Purpose**: A domain event represented as an exception instance.
* **Constructor**: `SideEffectException(string message, Exception? innerException = null)`
* **Members**:
* `Guid Id { get; }`: A stable identity for correlation. This remains unchanged even if the same instance is re-thrown.
* `DateTimeOffset OccurredAt { get; }`: The UTC creation timestamp of the exception.



### 1.3 Registration Pipeline

#### `ISideEffectRegistry`

* **Purpose**: Registers exception/handler pairs with dispatch conditions (filters).
* **Method**: `IDisposable Register<TException, THandler>(ISideEffectFilter filter)`
* `TException : SideEffectException`
* `THandler : class, ISideEffectHandler<TException>`
* **Return value**: `IDisposable` (Dispose to unregister the mapping)



#### `ISideEffectFilter`

* **Purpose**: Evaluates whether a handler should process a specific exception instance.
* **Method**: `bool Match(SideEffectException exception)`

#### `ISideEffectHandler<TException>`

* **Purpose**: Executes the peripheral reaction logic asynchronously.
* **Method**: `ValueTask HandleAsync(TException exception, CancellationToken cancellationToken = default)`

### 1.4 Configuration Options

#### `SideEffectOptions`

* `int QueueCapacity { get; init; }`: Capacity of the underlying `System.Threading.Channels`. Values less than or equal to `0` mean an unbounded queue.
* `int WorkerCount { get; init; }`: Number of background workers processing the queue (Default: `1`).
* `BoundedChannelFullMode FullMode { get; init; }`: Behavior when the queue capacity is reached (Default: `BoundedChannelFullMode.Wait`).

---

## 2. Development & Operational Guidelines (Guardrails)

### ⚠️ Strict Performance Warning (Intended Usage)

In C#, throwing an exception introduces overhead due to stack trace generation and metadata collection. Do not use SideEffect as a control flow mechanism for regular validations or high-frequency conditional branches.

* **Low-Frequency Principle**: Restrict `SideEffectException` to **low-frequency, critical domain errors or exceptional cases**.
* **Discouraged Examples**: Email format validation, password mismatch, or normal out-of-stock scenarios. These should be handled using lightweight alternatives like the `Result<T>` pattern.
* **Recommended Examples**: Payment gateway timeouts, fraudulent activity detection, or critical data corruption.

> 💡 **Documentation Snippet:**
> > ⚠️ **[Performance Warning]**
> > `SideEffectException` involves stack trace generation costs within the C# runtime. Do not use this framework for routine input validation or standard business flow redirection. Apply it to **low-frequency domain errors**. High-frequency errors should favor lightweight alternatives such as the `Result<T>` pattern.
> 
> 

---

### 📖 Static Analysis and Code Comprehension

SideEffect abstracts peripheral logic away from the main business flow. When an exception is caught locally and seemingly ignored—such as `catch (PaymentFailedException) { /* ignore */ }`—the asynchronous pipeline is designed to observe and process the exception in the background.

When reading the code, **there is no regular need to trace deep into the method call stack.** The side effects are **declaratively** organized in the following places:

1. **`*Exception.cs`**: The definition of the exception class, which defines the domain context.
2. **`SideEffectBootstrapper.cs`**: The routing table showing which exception maps to which filter and handler.

---

### 🧪 Testing Strategy Guidelines

The library infrastructure is responsible for ensuring that observed exceptions are captured and forwarded to the asynchronous queue.

Attempting to write automated integration tests (IT) or end-to-end (E2E) tests that span across the asynchronous pipeline to verify business logic is generally **discouraged**. Instead, separate your verification into three distinct, deterministic testing layers:

* **Logic Testing**: Verify **only** that the correct `SideEffectException` is thrown (or caught and handled locally) under expected conditions.
* **Filter Testing**: Verify that `Match()` returns the expected `bool` value against specific exception payloads.
* **Handler Testing**: Bypass the runtime entirely. Directly pass the exception object into the handler class instance and verify its internal processing logic (such as external API invocations).

---

## 3. Best Practices in a Dependency Injection (DI) Environment

1. **Composition Root**: Keep runtime creation and initialization inside `Program.cs` or the main startup module.
2. **Centralized Registry Wiring**: Consolidate filter and handler registrations into a single bootstrapper class (implementing `IHostedService`).
3. **Stateless Handlers**: Register handlers as singletons where possible. If a handler requires scoped components (e.g., `DbContext`), inject `IServiceScopeFactory` and manage the scope locally inside `HandleAsync`.

### Structural Blueprint

* `Program.cs` (Startup):
* Register services, add the SideEffect runtime as a singleton, and register `SideEffectBootstrapper` as a HostedService.


* `SideEffectBootstrapper.cs`:
* Execute all `Registry.Register<,>()` mappings within `StartAsync`.


* `*Exception.cs`:
* Define domain events inheriting from `SideEffectException` near their respective use cases.


* `*Handler.cs` / `*Filter.cs`:
* Implement concrete side-effect logic and dispatch conditions within the application or infrastructure layer.



---

## 4. Implementation Example (DI)

### Program.cs

```cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SideEffect;

var builder = Host.CreateApplicationBuilder(args);

// Register handlers and filters
builder.Services.AddSingleton<PaymentAlertHandler>();
builder.Services.AddSingleton<MatchAllFilter>();

// Register the runtime instance
builder.Services.AddSingleton(sp =>
    new SideEffect.SideEffect(
        handlerResolver: type => sp.GetService(type),
        options: new SideEffectOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1024
        }));

// HostedService to handle registration at startup
builder.Services.AddHostedService<SideEffectBootstrapper>();

await using var app = builder.Build();
await app.RunAsync();

```

### SideEffectBootstrapper.cs

```cs
using Microsoft.Extensions.Hosting;
using SideEffect;

public sealed class SideEffectBootstrapper(
    SideEffect.SideEffect sideEffect,
    MatchAllFilter filter)
    : IHostedService, IDisposable
{
    private IDisposable? _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // This centralized location outlines exception-to-handler mappings
        _registration = sideEffect.Registry.Register<PaymentFailedException, PaymentAlertHandler>(filter);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _registration?.Dispose();
    }
}

```

### Use Case Scenario (Throw + Local Recovery)

```cs
public sealed class PaymentService
{
    public void Pay()
    {
        // Pattern A: Throw and perform local recovery
        // The main path handles the exception synchronously, while the background pipeline
        try
        {
            ProcessPayment();
        }
        catch(PaymentFailedException ex)
        {
            // Synchronous recovery (e.g., UI updates or fallback flow)
            LocalRecovery(ex); 
        }

        // Pattern B: Suppress the exception locally
        // While the main path handles the exception silently, the background pipeline 
        // routes the thrown exception instance to the registered PaymentAlertHandler.
        try { ProcessPayment(); } 
          catch(PaymentFailedException) { /* ignore */ }
    }
}

```

---

## 5. Behavior Guarantees

The SideEffect runtime enforces the following behaviors, validated by tests within `SideEffect.Tests`:

* Local `catch` blocks and asynchronous side-effect processing operate concurrently without interference.
* If the same exception instance is re-thrown (`throw;`), it is queued for background processing **only once**.
* Exceptions that do not satisfy `ISideEffectFilter.Match` are excluded from dispatching and are dropped immediately.
* A failure or unhandled exception within one handler does not block or impact the execution of other registered handlers.
* Once the runtime is disposed of via `Dispose`/`DisposeAsync`, observation of new exceptions stops.
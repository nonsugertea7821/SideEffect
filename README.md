## Why SideEffect? (The Core Philosophy)

When dealing with exceptions and cross-cutting concerns, developers usually face a frustrating, binary dilemma:

1. **Propagate to the Top Level:** Keep local code clean and centralized, but **lose the ability to recover locally**. The operation fails completely, and the user gets a generic error screen.
2. **Catch and Handle Locally:** Gracefully recover and return a fallback/default value, but **clutter your business logic** with telemetry, logging, and notification code. If you forget to copy-paste those lines into a new `catch` block, your system goes blind to that failure.

### The Sweet Spot

**SideEffect was built to bridge this exact gap.** It delivers the ultimate "sweet spot" that developers have always wanted:

> *"I want to catch and handle errors locally to gracefully recover the user experience, but I still want the resulting facts (logs, metrics, alerts) to be processed safely in the background, governed by centralized policies, without messing up my primary business flow."*

### How It Works Under the Hood

With SideEffect, your business logic doesn't choose between recovery and observability. It gets both.

```text
    [ Your Business Logic ]
           │
           ▼ (An Exception Occurs)
┌─────────────────────────────────────────┐
│  SideEffect.Notify(exception);          │ ──► [ SideEffect Pipeline ] (Safe & Async)
└─────────────────────────────────────────┘          │
           │                                         ├─► Send Slack Alert
           ▼ (Graceful Recovery)                     ├─► Increment Telemetry Metrics
return DefaultFallbackValue;                         └─► Log to Secure Audit Trail

```

By publishing the exception as a raw **Fact** to SideEffect, your business logic can immediately move on and recover. Meanwhile, the SideEffect pipeline evaluates your centralized **Policies** independently, ensuring that an error in a slack notification never crashes your primary application thread.
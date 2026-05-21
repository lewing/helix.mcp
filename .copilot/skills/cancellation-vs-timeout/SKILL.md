# Skill: Distinguishing HTTP Timeout from Cancellation in .NET

**Confidence:** low
**Source:** earned

## Problem

When wrapping async HTTP calls with `CancellationToken` support, both HTTP timeouts and intentional cancellation throw `TaskCanceledException`. You need to distinguish them to provide different error messages (e.g., "request timed out" vs. letting cancellation propagate).

## Wrong Pattern

```csharp
catch (TaskCanceledException ex) when (ex.CancellationToken == cancellationToken)
{
    throw; // Intended as "real cancellation"
}
catch (TaskCanceledException ex)
{
    throw new MyException("Request timed out.", ex); // Intended as timeout
}
```

**Why it fails:** When `cancellationToken` is `CancellationToken.None` (from `= default`) and the `TaskCanceledException` is constructed without a token (also `CancellationToken.None`), the equality check matches — treating a timeout as cancellation.

## Correct Pattern

```csharp
catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw; // Real cancellation — the caller's token was triggered
}
catch (TaskCanceledException ex)
{
    throw new MyException("Request timed out.", ex); // HTTP timeout
}
```

**Why it works:** `IsCancellationRequested` checks whether the *caller's* token was actually signaled. If it wasn't, the `TaskCanceledException` must have come from an HTTP timeout. This is semantically correct regardless of how the exception was constructed.

## When to Apply

Any time you're catching `TaskCanceledException` in a method with a `CancellationToken cancellationToken = default` parameter and need to distinguish timeout from cancellation.

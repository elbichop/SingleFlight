# SingleFlight.Net

[![NuGet](https://img.shields.io/nuget/v/SingleFlight.svg)](https://www.nuget.org/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SingleFlight.svg)](https://www.nuget.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Lightweight async SingleFlight implementation for .NET.

`SingleFlight.Net` prevents duplicate concurrent execution of the same operation by coalescing callers that use the same key.

If 100 callers request the same resource at the same time, only one underlying operation runs. The result is shared with all callers.

## Features

* Coalesces concurrent operations by key.
* Async-first API.
* Generic result types.
* Per-caller cancellation.
* Shared operation cancellation is independent from caller cancellation.
* Exceptions are shared with callers waiting for the same operation.
* Thread-safe.
* No external dependencies.
* Supports .NET 8, .NET 9 and .NET 10.

## Installation

```bash
dotnet add package SingleFlight.Net
```

Or:

```powershell
Install-Package SingleFlight.Net
```

## Basic usage

```csharp
using SingleFlight;

var singleFlight = new SingleFlightExecutor<Product>();

var product = await singleFlight.RunAsync(
    "product:123",
    () => GetProductAsync(123));
```

If several callers execute the same operation concurrently:

```csharp
var tasks = Enumerable.Range(0, 100)
    .Select(_ =>
        singleFlight.RunAsync(
            "product:123",
            () => GetProductAsync(123)));

var products = await Task.WhenAll(tasks);
```

`GetProductAsync(123)` is executed once. All callers receive the same result.

## How it works

SingleFlight keeps track of operations currently running for each key.

```text
Caller A ─┐
Caller B ─┼──> "product:123" ──> One operation
Caller C ─┘                         │
                                    └──> Shared result
```

The behavior is:

1. The first caller for a key starts the operation.
2. Concurrent callers using the same key join the existing operation.
3. Different keys can execute independently and concurrently.
4. When the operation completes, the key is removed.
5. A later request for the same key can start a new operation.

SingleFlight coordinates **in-flight work**. It does not cache completed results.

## Cancellation

Each caller can cancel its own wait:

```csharp
var product = await singleFlight.RunAsync(
    "product:123",
    () => GetProductAsync(123),
    cancellationToken);
```

Cancelling this token cancels the **caller's wait**, not the shared operation.

For operations that need their own cancellation token, use the overload that provides an operation token:

```csharp
var product = await singleFlight.RunAsync(
    "product:123",
    async operationToken =>
    {
        return await GetProductAsync(123, operationToken);
    },
    cancellationToken);
```

The two tokens have different responsibilities:

* `cancellationToken` — controls the current caller's wait.
* `operationToken` — belongs to the shared operation and is independent of caller cancellation.

Therefore, if Caller A cancels, Callers B and C can continue waiting for the same operation.

## Exceptions

If the shared operation fails, callers waiting for that operation observe the same failure.

Once the operation completes, the key is removed, so a subsequent call can start a new operation.

## Choosing a key

The key identifies work that can safely be shared between concurrent callers.

For example:

```csharp
"product:123"
```

If the result depends on additional scope, include that scope in the key:

```csharp
"tenant:42:product:123"
```

The key should represent the scope of the data being shared.

## Typical use cases

SingleFlight is useful when concurrent callers may request the same expensive resource:

* HTTP requests
* Database queries
* External service calls
* Configuration or metadata loading
* Expensive computations
* Cache-miss protection

A common pattern is:

```text
Request
   │
   ▼
 Cache
   │
   ├── Hit ──────> Result
   │
   └── Miss
        │
        ▼
   SingleFlight
        │
        ▼
   Backend/API
```

SingleFlight prevents multiple concurrent cache misses from triggering the same backend operation.

## In-process only

`SingleFlight.Net` coordinates operations **within a single process**.

If your application runs multiple instances:

```text
          Load Balancer
          /     |     \
       App A  App B  App C
         │      │      │
     SingleFlight
```

each process has its own in-flight operations.

The same key can therefore execute once per process.

SingleFlight.Net does not provide distributed locking or distributed request coalescing.

## What SingleFlight is not

SingleFlight is not a replacement for:

* caching;
* rate limiting;
* distributed locks;
* queues;
* retries;
* circuit breakers.

Each solves a different problem and they can be combined.

## Performance

The implementation is intentionally small and lightweight.

The main coordination path consists of:

* an in-memory lookup by key;
* synchronization of the in-flight operation collection;
* sharing the existing `Task<T>` between callers;
* removing the key after completion.

The purpose of SingleFlight is not to make an individual operation faster. Its purpose is to avoid executing the same expensive operation multiple times concurrently.

Benchmarks are available in the repository.

## API

```csharp
public sealed class SingleFlightExecutor<T>
{
    public Task<T> RunAsync(
        string key,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default);

    public Task<T> RunAsync(
        string key,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}
```

The API is intentionally small and focused on one responsibility:

> Prevent duplicate concurrent execution of the same operation for a given key.

## License

MIT License.

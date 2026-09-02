# SingleFlight

[![NuGet](https://img.shields.io/nuget/v/SingleFlight.svg)](https://www.nuget.org/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SingleFlight.svg)](https://www.nuget.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A lightweight **SingleFlight implementation for .NET** that coalesces concurrent asynchronous operations for the same key into a single in-flight operation.

Instead of executing the same expensive operation multiple times when many callers request the same resource concurrently, `SingleFlight` executes it once and shares the result with all callers waiting for that key.

Built for modern .NET applications where duplicate concurrent work can create unnecessary load on APIs, databases, external services, or other expensive resources.

---

## Why SingleFlight?

Consider an application receiving 1,000 concurrent requests for the same resource:

```text
1,000 incoming requests
        ?
        ??? Request 1  ???
        ??? Request 2  ???
        ??? Request 3  ???
        ?       ...      ?
        ??? Request 1000 ?
                ?
                ?
          External API
```

Without request coalescing, all 1,000 requests may execute the same operation.

With `SingleFlight`:

```text
1,000 incoming requests
        ?
        ?
    SingleFlight
        ?
        ????????????????
                       ?
                1 underlying operation
                       ?
                       ?
                 Shared result
                       ?
          ???????????????????????????
          ?            ?            ?
       Caller 1     Caller 2     Caller 1000
```

For a given key, only **one operation is in flight at a time**.

---

## Features

* Coalesces concurrent asynchronous operations by key.
* Prevents duplicate work for identical concurrent requests.
* Supports per-caller `CancellationToken`.
* Cancellation of one caller does not cancel the shared operation.
* Exceptions are propagated to all callers waiting for the same operation.
* Uses a lightweight in-memory dictionary protected by `Lock`.
* No external dependencies.
* Designed for modern .NET applications.
* Works particularly well alongside caching.
* Useful for HTTP calls, database queries, expensive computations and external services.

---

## Requirements

* .NET 10 or later

---

## Installation

Install the package using the .NET CLI:

```bash
dotnet add package SingleFlight
```

Or using Visual Studio Package Manager:

```powershell
Install-Package SingleFlight
```

---

## Basic Usage

Create an executor for the result type you want to coalesce:

```csharp
using SingleFlight;

var singleFlight = new SingleFlightExecutor<Product>();

var product = await singleFlight.RunAsync(
    "product:123",
    async () =>
    {
        return await GetProductAsync(123);
    });
```

If several callers execute this concurrently:

```csharp
await Task.WhenAll(
    singleFlight.RunAsync("product:123", () => GetProductAsync(123)),
    singleFlight.RunAsync("product:123", () => GetProductAsync(123)),
    singleFlight.RunAsync("product:123", () => GetProductAsync(123))
);
```

only one `GetProductAsync(123)` operation is executed.

All callers receive the same resulting `Product`.

---

## How It Works

`SingleFlightExecutor<T>` maintains a collection of currently executing operations:

```csharp
Dictionary<string, Task<T>>
```

The key identifies the resource being requested.

When `RunAsync` is called:

1. `SingleFlight` checks whether an operation for the key is already running.
2. If one exists, the caller waits for that existing task.
3. If no operation exists, a new operation is created.
4. The operation is registered for that key.
5. All concurrent callers receive the same in-flight task.
6. Once the operation completes, the key is removed.

Conceptually:

```text
RunAsync("product:123")
        ?
        ?
Is "product:123" already running?
        ?
   ???????????
   ?         ?
  Yes        No
   ?         ?
   ?         ?
Wait for   Start operation
existing   and register it
task           ?
   ?           ?
   ?       Execute operation
   ?           ?
   ?????????????
         ?
    Return result
         ?
         ?
Remove key from in-flight operations
```

---

## Concurrent Requests

The main purpose of `SingleFlight` is handling concurrent requests for the **same key**.

For example:

```csharp
var tasks = Enumerable.Range(0, 1_000)
    .Select(_ =>
        singleFlight.RunAsync(
            "product:123",
            () => GetProductAsync(123)));

var results = await Task.WhenAll(tasks);
```

Even though there are 1,000 callers, there is only one underlying operation for:

```text
product:123
```

The result is then shared among the callers.

---

## Different Keys

`SingleFlight` only coalesces operations that use the same key.

For example:

```csharp
var product1 = singleFlight.RunAsync(
    "product:1",
    () => GetProductAsync(1));

var product2 = singleFlight.RunAsync(
    "product:2",
    () => GetProductAsync(2));

var product3 = singleFlight.RunAsync(
    "product:3",
    () => GetProductAsync(3));

await Task.WhenAll(product1, product2, product3);
```

These are three different operations:

```text
product:1 ? operation A
product:2 ? operation B
product:3 ? operation C
```

They are **not** coalesced because they have different keys.

This is an important distinction:

> SingleFlight prevents duplicate concurrent work for the same key. It does not limit the total number of different operations.

---

## Cancellation

Each caller can provide its own `CancellationToken`:

```csharp
var product = await singleFlight.RunAsync(
    "product:123",
    () => GetProductAsync(123),
    cancellationToken);
```

Cancellation applies to the **caller waiting for the result**, not to the shared operation itself.

For example:

```text
Caller A ?????????
                 ?
Caller B ???????????? SingleFlight ??? Operation
                 ?
Caller C ?????????
```

If Caller A cancels:

```text
Caller A ??? cancelled
Caller B ??? waiting
Caller C ??? waiting
                 ?
                 ?
             Operation
                 ?
                 ?
             Result
```

The shared operation continues for the remaining callers.

This prevents one caller from accidentally cancelling work that other callers are depending on.

---

## Exceptions

If the underlying operation fails, the exception is propagated to the callers waiting for that operation.

For example:

```csharp
var result = await singleFlight.RunAsync(
    "product:123",
    async () =>
    {
        throw new InvalidOperationException("Backend unavailable");
    });
```

Concurrent callers waiting for `product:123` observe the failure of the shared operation.

After the operation completes, the key is removed from the in-flight collection.

A subsequent request can therefore start a new operation.

---

## SingleFlight and Caching

`SingleFlight` is **not a replacement for a cache**.

They solve different problems and work particularly well together.

A cache avoids performing work again because the result is already stored.

SingleFlight prevents multiple callers from performing the same work **concurrently**.

A common architecture is:

```text
                Request
                   ?
                   ?
                 Cache
                   ?
          ???????????????????
          ?                 ?
        Hit                Miss
          ?                 ?
          ?                 ?
       Return          SingleFlight
                            ?
                            ?
                      Backend/API
                            ?
                            ?
                         Cache
                            ?
                            ?
                     Return result
```

For example:

```csharp
var cached = await cache.GetAsync<Product>("product:123");

if (cached is not null)
    return cached;

return await singleFlight.RunAsync(
    "product:123",
    async () =>
    {
        var product = await GetProductAsync(123);

        await cache.SetAsync(
            "product:123",
            product);

        return product;
    });
```

This combination helps prevent a common **cache stampede** scenario.

### Without SingleFlight

If the cache entry expires and 1,000 requests arrive simultaneously:

```text
1,000 requests
      ?
      ?
  Cache miss
      ?
      ???? API
      ???? API
      ???? API
      ???? API
      ???? ... 1,000 calls
```

### With SingleFlight

```text
1,000 requests
      ?
      ?
  Cache miss
      ?
      ?
  SingleFlight
      ?
      ?
   1 API call
      ?
      ?
    Cache
      ?
      ?
 Shared result
```

---

## SingleFlight vs Cache vs Rate Limiter

These mechanisms should not be confused.

| Mechanism     | Main purpose                                     |
| ------------- | ------------------------------------------------ |
| Cache         | Avoid work by reusing previously stored results  |
| SingleFlight  | Avoid duplicate concurrent work for the same key |
| RateLimiter   | Control the rate/amount of outgoing operations   |
| Retry/Backoff | Handle transient failures                        |

They can be combined:

```text
Request
   ?
   ?
 Cache
   ?
   ?
SingleFlight
   ?
   ?
RateLimiter
   ?
   ?
External API
```

Each component solves a different problem.

---

## Rate Limits and Different Keys

Consider an external service that allows only one request per second.

If you have:

```text
1,000 callers
1,000 different keys
```

SingleFlight cannot reduce those requests because there is nothing to coalesce.

For example:

```text
product:1
product:2
product:3
...
product:1000
```

All keys are different.

In this scenario you need a rate limiter, queue, or another form of controlled concurrency.

SingleFlight is most effective when multiple concurrent callers are requesting the **same resource**.

---

## HTTP Example

A typical use case is protecting an external HTTP API.

```csharp
public sealed class ProductService
{
    private readonly HttpClient _httpClient;
    private readonly SingleFlightExecutor<Product> _singleFlight;

    public ProductService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _singleFlight = new SingleFlightExecutor<Product>();
    }

    public Task<Product> GetProductAsync(
        int productId,
        CancellationToken cancellationToken = default)
    {
        return _singleFlight.RunAsync(
            $"product:{productId}",
            async () =>
            {
                return await _httpClient.GetFromJsonAsync<Product>(
                    $"products/{productId}",
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        "Product was not returned.");
            },
            cancellationToken);
    }
}
```

If 500 callers request:

```text
product:1
```

at approximately the same time, only one HTTP operation is executed.

If another 300 callers request:

```text
product:2
```

and another 200 request:

```text
product:3
```

the expected number of underlying operations is:

```text
product:1 ? 1
product:2 ? 1
product:3 ? 1

Total ? 3
```

instead of:

```text
1,000 callers ? 1,000 HTTP requests
```

---

## Authentication and Keys

The key should represent the **scope of the result**, not necessarily the caller.

For example, if:

```text
GET /products/123
```

returns exactly the same product regardless of which user requests it, a suitable key can be:

```text
product:123
```

Even if every caller has a different authentication token, the token does not need to be part of the SingleFlight key **if the response is truly identical for all callers**.

However, if the response depends on:

* user identity
* tenant
* permissions
* authorization scope
* subscription
* locale
* account
* any other caller-specific state

then that scope should be represented in the key.

For example:

```text
tenant:42:product:123
```

or:

```text
user:123:profile
```

The key must represent the data that can safely be shared between callers.

---

## Distributed Applications

`SingleFlight` is an **in-process** coordination mechanism.

For example, if an application is running with three instances:

```text
             Load Balancer
                  ?
       ???????????????????????
       ?          ?          ?
     Pod A       Pod B      Pod C
       ?          ?          ?
   SingleFlight SingleFlight SingleFlight
```

A request for the same key can be coalesced within each process, but the processes do not share their in-flight dictionaries.

Therefore:

```text
Pod A ? 1 backend request
Pod B ? 1 backend request
Pod C ? 1 backend request
```

There can be up to one underlying operation per process.

If coordination must happen across multiple application instances, a distributed coordination mechanism is required.

For distributed caching scenarios, Redis or another distributed store can complement SingleFlight, depending on the architecture.

---

## Thread Safety

`SingleFlightExecutor<T>` is designed to be safely used by concurrent callers.

The in-flight operation collection is protected by a synchronization lock:

```csharp
private readonly Dictionary<string, Task<T>> _operations = new();
private readonly Lock _lock = new();
```

The lock only protects access to the in-flight dictionary.

The underlying asynchronous operation itself is not executed while holding the lock.

This keeps the synchronization scope small and avoids blocking other callers while the actual operation is running.

---

## Performance

The implementation is intentionally small and focuses on the critical path:

* one in-memory dictionary lookup
* synchronization around the in-flight collection
* reuse of the existing `Task<T>` for concurrent callers
* removal of the key when the operation completes

The goal is not to make the operation itself faster.

The goal is to prevent **unnecessary duplicate execution**.

For expensive operations such as:

* HTTP requests
* database queries
* external API calls
* remote service calls
* expensive computations

the cost of the coordination layer is typically negligible compared with executing the duplicated operation.

---

## Benchmark

The implementation was benchmarked against `FastPatterns.SingleFlight<TKey, TResult>`.

The benchmark includes:

* concurrent callers
* completed operations
* asynchronous operations
* multiple request counts
* different keys
* allocation measurements

For the generic implementation, the results were effectively equivalent to the reference implementation in the tested scenarios.

Example results from a 1,000-call benchmark:

```text
| Method                       | Mean         | Allocated |
|----------------------------- |-------------:|----------:|
| OurSingleFlight              | 15,477.43 us |  40.4 KB  |
| FastPatterns                 | 15,481.42 us |  40.4 KB  |
```

For completed operations:

```text
| Method                       | Mean     | Allocated |
|----------------------------- |---------:|----------:|
| OurSingleFlightCompleted     | 41.04 us |  23.66 KB |
| FastPatternsCompleted        | 41.24 us |  23.66 KB |
```

The important result is that the implementation does not introduce a significant performance difference compared with the reference implementation in these tests.

### Real HTTP scenario

A separate test used 1,000 concurrent callers distributed across three keys:

```text
500 × product:1
300 × product:2
200 × product:3
```

Without SingleFlight:

```text
Callers:        1,000
HTTP requests:  1,000
```

With SingleFlight:

```text
Callers:        1,000
HTTP requests:      3
```

That represents a:

```text
99.7% reduction in outbound HTTP requests
```

The important metric in this scenario is the number of underlying operations, not raw wall-clock time, because network latency and external service behavior can dominate end-to-end timing.

---

## When Should I Use SingleFlight?

`SingleFlight` is a good fit when:

* many callers can request the same resource concurrently;
* the underlying operation is expensive;
* the result can safely be shared between those callers;
* duplicate concurrent requests create unnecessary load;
* you are protecting an external API;
* you want to reduce cache-miss amplification;
* you are performing expensive database or computation operations.

Typical examples:

```text
HTTP API requests
Database queries
Configuration loading
Metadata retrieval
Token/public-key discovery
Expensive calculations
External service calls
Cache stampede protection
```

---

## When Should I Not Use It?

SingleFlight is not a replacement for:

* distributed locks;
* distributed caching;
* rate limiting;
* queues;
* retries;
* circuit breakers;
* request deduplication across multiple processes.

It is also not particularly useful when every request has a unique key:

```text
request:1
request:2
request:3
request:4
...
```

There is nothing to coalesce.

---

## API

The main API is intentionally small:

```csharp
public sealed class SingleFlightExecutor<T>
{
    public Task<T> RunAsync(
        string key,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default);
}
```

### Parameters

#### `key`

Identifies the resource or operation being requested.

Concurrent callers using the same key share the same in-flight operation.

#### `operation`

The asynchronous operation that should be executed if there is no existing operation for the key.

#### `cancellationToken`

Cancels the current caller's wait for the result.

It does not cancel the shared operation.

---

## Design Goals

The library intentionally follows a small set of principles:

### Simple

The public API should be easy to understand and integrate.

### Lightweight

No external dependencies are required.

### Predictable

The same key represents the same in-flight operation.

### Composable

SingleFlight should work alongside existing application infrastructure such as:

```text
Cache
RateLimiter
Retry
Circuit Breaker
HTTP Client
Database
```

### Focused

The library solves one problem:

> Prevent duplicate concurrent execution of the same operation.

---

## License

This project is licensed under the [MIT License](LICENSE).

---

## Contributing

Contributions, bug reports and suggestions are welcome.

If you find a problem or have an idea for improving the library, please open an issue or submit a pull request.

---

## Disclaimer

`SingleFlight` provides **in-process request coalescing**.

It does not provide distributed coordination between multiple application instances.

Always verify the requirements and limitations of the external services you consume, especially when they enforce request-rate, authentication, IP, session or concurrency restrictions.

---

## Summary

`SingleFlight` provides a simple way to prevent duplicate concurrent work:

```text
Without SingleFlight:

1000 callers
     ?
     ???????? 1000 operations


With SingleFlight:

1000 callers
     ?
     ?
SingleFlight
     ?
     ???????? 1 operation
                    ?
                    ?
              Shared result
```

For applications dealing with expensive or rate-limited operations, combining:

```text
Cache + SingleFlight + RateLimiter
```

can significantly reduce unnecessary backend work while keeping the application architecture simple and predictable.

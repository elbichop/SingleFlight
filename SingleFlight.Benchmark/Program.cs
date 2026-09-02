using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

public static class Program
{
    private const int TotalCallers = 1000;

    public static async Task Main()
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://dummyjson.com"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        Console.WriteLine();
        Console.WriteLine("==============================================================================================");
        Console.WriteLine(" SINGLEFLIGHT - REAL API BENCHMARK");
        Console.WriteLine("==============================================================================================");
        Console.WriteLine();

        Console.WriteLine($"API:          DummyJSON");
        Console.WriteLine($"Callers:      {TotalCallers:N0}");
        Console.WriteLine();
        Console.WriteLine("Distribution:");
        Console.WriteLine("  product:1   500 callers");
        Console.WriteLine("  product:2   300 callers");
        Console.WriteLine("  product:3   200 callers");
        Console.WriteLine();
        Console.WriteLine("Each caller uses a different Bearer token.");
        Console.WriteLine("SingleFlight key = product ID.");
        Console.WriteLine();

        // Warm-up
        await WarmupAsync(httpClient);

        Console.WriteLine("Running...");
        Console.WriteLine();

        var results = new List<ScenarioResult>();

        results.Add(
            await RunScenarioAsync(
                "Direct",
                httpClient,
                useOurSingleFlight: false,
                useFastPatterns: false));

        await Task.Delay(1000);

        results.Add(
            await RunScenarioAsync(
                "OurSingleFlight",
                httpClient,
                useOurSingleFlight: true,
                useFastPatterns: false));

        await Task.Delay(1000);

        results.Add(
            await RunScenarioAsync(
                "FastPatterns",
                httpClient,
                useOurSingleFlight: false,
                useFastPatterns: true));

        PrintResults(results);

        Console.WriteLine();
        Console.WriteLine("==============================================================================================");
        Console.WriteLine(" EXPECTED COALESCING");
        Console.WriteLine("==============================================================================================");
        Console.WriteLine();
        Console.WriteLine("Direct:");
        Console.WriteLine("  500 callers -> product:1 -> 500 HTTP requests");
        Console.WriteLine("  300 callers -> product:2 -> 300 HTTP requests");
        Console.WriteLine("  200 callers -> product:3 -> 200 HTTP requests");
        Console.WriteLine("  Total = 1000 HTTP requests");
        Console.WriteLine();
        Console.WriteLine("SingleFlight:");
        Console.WriteLine("  500 callers -> product:1 -> 1 HTTP request");
        Console.WriteLine("  300 callers -> product:2 -> 1 HTTP request");
        Console.WriteLine("  200 callers -> product:3 -> 1 HTTP request");
        Console.WriteLine("  Total = 3 HTTP requests");
        Console.WriteLine();
    }

    private static async Task WarmupAsync(HttpClient httpClient)
    {
        try
        {
            using var response = await httpClient.GetAsync("/products/1");

            Console.WriteLine(
                $"Warm-up: {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warm-up failed: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        string name,
        HttpClient httpClient,
        bool useOurSingleFlight,
        bool useFastPatterns)
    {
        var requests = CreateRequests();

        var counters = new HttpCounters();

        var tasks = new Task<Product?>[TotalCallers];

        // Barrier para que todos los callers estén preparados
        // antes de comenzar.
        var startGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var stopwatch = Stopwatch.StartNew();

        if (useOurSingleFlight)
        {
            var singleFlight = new SingleFlightExecutor<Product?>();

            for (var i = 0; i < requests.Length; i++)
            {
                var request = requests[i];

                tasks[i] = RunCallerAsync(
                    startGate,
                    () => singleFlight.RunAsync(
                        request.Key,
                        ct => GetProductAsync(
                            httpClient,
                            request.ProductId,
                            request.Token,
                            ct,
                            counters)));
            }
        }
        else if (useFastPatterns)
        {
            var singleFlight =
                new FastPatterns.Observer.SingleFlight<string, Product?>();

            for (var i = 0; i < requests.Length; i++)
            {
                var request = requests[i];

                tasks[i] = RunCallerAsync(
                    startGate,
                    () => singleFlight.RunAsync(
                        request.Key,
                        ct => GetProductAsync(
                            httpClient,
                            request.ProductId,
                            request.Token,
                            ct,
                            counters)));
            }
        }
        else
        {
            for (var i = 0; i < requests.Length; i++)
            {
                var request = requests[i];

                tasks[i] = RunCallerAsync(
                    startGate,
                    () => GetProductAsync(
                        httpClient,
                        request.ProductId,
                        request.Token,
                        CancellationToken.None,
                        counters));
            }
        }

        // Todos empiezan aquí.
        startGate.SetResult();

        var results = await Task.WhenAll(tasks);

        stopwatch.Stop();

        var validResults = results.Count(x => x is not null);

        var httpReduction =
            100.0 *
            (1.0 - (double)counters.HttpRequests / TotalCallers);

        return new ScenarioResult
        {
            Name = name,
            Callers = TotalCallers,
            HttpRequests = counters.HttpRequests,
            Http200 = counters.Http200,
            Http429 = counters.Http429,
            HttpErrors = counters.HttpErrors,
            ValidResults = validResults,
            HttpReduction = httpReduction,
            Elapsed = stopwatch.Elapsed
        };
    }

    private static async Task<Product?> GetProductAsync(
        HttpClient httpClient,
        int productId,
        string token,
        CancellationToken cancellationToken,
        HttpCounters counters)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/products/{productId}");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // Cuenta la petición HTTP REAL.
        Interlocked.Increment(ref counters.HttpRequests);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Interlocked.Increment(ref counters.Http429);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref counters.HttpErrors);
                return null;
            }

            Interlocked.Increment(ref counters.Http200);

            await using var stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);

            return await JsonSerializer.DeserializeAsync<Product>(
                stream,
                cancellationToken: cancellationToken);
        }
        catch
        {
            Interlocked.Increment(ref counters.HttpErrors);
            return null;
        }
    }

    private static async Task<Product?> RunCallerAsync(
        TaskCompletionSource startGate,
        Func<Task<Product?>> operation)
    {
        await startGate.Task;

        return await operation();
    }

    private static ApiRequest[] CreateRequests()
    {
        var requests = new ApiRequest[TotalCallers];

        var index = 0;

        for (var i = 0; i < 500; i++)
        {
            index++;

            requests[index - 1] = new ApiRequest
            {
                Key = "product:1",
                ProductId = 1,
                Token = $"token-{index}"
            };
        }

        for (var i = 0; i < 300; i++)
        {
            index++;

            requests[index - 1] = new ApiRequest
            {
                Key = "product:2",
                ProductId = 2,
                Token = $"token-{index}"
            };
        }

        for (var i = 0; i < 200; i++)
        {
            index++;

            requests[index - 1] = new ApiRequest
            {
                Key = "product:3",
                ProductId = 3,
                Token = $"token-{index}"
            };
        }

        return requests;
    }

    private static void PrintResults(
        List<ScenarioResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("==============================================================================================================");
        Console.WriteLine("| Method          | Callers | HTTP | 200 | 429 | Errors | Valid | Reduction | Time           | HTTP/Caller |");
        Console.WriteLine("|-----------------|--------:|-----:|----:|----:|-------:|------:|----------:|--------------:|------------:|");

        foreach (var result in results)
        {
            var httpPerCaller =
                (double)result.HttpRequests / result.Callers;

            Console.WriteLine(
                $"| {result.Name,-15} " +
                $"| {result.Callers,7:N0} " +
                $"| {result.HttpRequests,5:N0} " +
                $"| {result.Http200,3:N0} " +
                $"| {result.Http429,3:N0} " +
                $"| {result.HttpErrors,7:N0} " +
                $"| {result.ValidResults,6:N0} " +
                $"| {result.HttpReduction,8:F2}% " +
                $"| {result.Elapsed.TotalMilliseconds,12:N2} ms " +
                $"| {httpPerCaller,11:F4} |");
        }

        Console.WriteLine("==============================================================================================================");
    }
}

public sealed class SingleFlightExecutor<TResult>
{
    private readonly Dictionary<string, Task<TResult>> _inflight = new();
    private readonly Lock _lock = new();

    public Task<TResult> RunAsync(
        string key,
        Func<CancellationToken, Task<TResult>> factory,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_inflight.TryGetValue(key, out var existing))
                return existing;

            var task = RunCoreAsync(
                key,
                factory,
                cancellationToken);

            _inflight[key] = task;

            return task;
        }
    }

    private async Task<TResult> RunCoreAsync(
        string key,
        Func<CancellationToken, Task<TResult>> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await factory(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _inflight.Remove(key);
            }
        }
    }
}

public sealed class HttpCounters
{
    public int HttpRequests;
    public int Http200;
    public int Http429;
    public int HttpErrors;
}

public sealed class ApiRequest
{
    public string Key { get; init; } = "";
    public int ProductId { get; init; }
    public string Token { get; init; } = "";
}

public sealed class Product
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

public sealed class ScenarioResult
{
    public string Name { get; init; } = "";
    public int Callers { get; init; }
    public int HttpRequests { get; init; }
    public int Http200 { get; init; }
    public int Http429 { get; init; }
    public int HttpErrors { get; init; }
    public int ValidResults { get; init; }
    public double HttpReduction { get; init; }
    public TimeSpan Elapsed { get; init; }
}
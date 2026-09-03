using SingleFlight;

namespace SingleFlight.Tests;

public class SingleFlightTests
{
    [Fact]
    public async Task SameKey_ConcurrentCalls_ExecutesOperationOnlyOnce()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation()
        {
            Interlocked.Increment(ref executions);

            await Task.Delay(100);

            return 42;
        }

        var tasks = Enumerable
            .Range(0, 100)
            .Select(_ => singleFlight.RunAsync("same-key", Operation));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, executions);
        Assert.Equal(100, results.Length);
        Assert.All(results, result => Assert.Equal(42, result));
    }

    [Fact]
    public async Task DifferentKeys_ExecuteOperationsIndependently()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation()
        {
            Interlocked.Increment(ref executions);

            await Task.Delay(100);

            return 42;
        }

        var task1 = singleFlight.RunAsync("key-1", Operation);
        var task2 = singleFlight.RunAsync("key-2", Operation);

        var results = await Task.WhenAll(task1, task2);

        Assert.Equal(2, executions);
        Assert.All(results, result => Assert.Equal(42, result));
    }

    [Fact]
    public async Task SameKey_1000ConcurrentCalls_ExecutesOperationOnlyOnce()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation()
        {
            Interlocked.Increment(ref executions);

            await Task.Delay(100);

            return 42;
        }

        var tasks = Enumerable
            .Range(0, 1000)
            .Select(_ => singleFlight.RunAsync("same-key", Operation));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, executions);
        Assert.Equal(1000, results.Length);
        Assert.All(results, result => Assert.Equal(42, result));
    }

    [Fact]
    public async Task DifferentKeys_ExecuteInParallel()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var started = 0;

        async Task<int> Operation()
        {
            Interlocked.Increment(ref started);

            await Task.Delay(500);

            return 42;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var task1 = singleFlight.RunAsync("key-1", Operation);
        var task2 = singleFlight.RunAsync("key-2", Operation);

        var results = await Task.WhenAll(task1, task2);

        stopwatch.Stop();

        Assert.Equal(2, started);
        Assert.Equal(42, results[0]);
        Assert.Equal(42, results[1]);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(800),
            $"Operations took {stopwatch.Elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task FailedOperation_AllowsNewExecution()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation()
        {
            var execution = Interlocked.Increment(ref executions);

            await Task.Delay(100);

            if (execution == 1)
            {
                throw new InvalidOperationException(
                    "Something went wrong");
            }

            return 42;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => singleFlight.RunAsync("same-key", Operation));

        var result = await singleFlight.RunAsync(
            "same-key",
            Operation);

        Assert.Equal(2, executions);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task SameKey_ConcurrentFailedCalls_ExecutesOperationOnlyOnce()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation()
        {
            Interlocked.Increment(ref executions);

            await Task.Delay(100);

            throw new InvalidOperationException(
                "Operation failed");
        }

        var tasks = Enumerable
            .Range(0, 100)
            .Select(_ =>
                singleFlight.RunAsync("same-key", Operation));

        var exceptions = await Task.WhenAll(
            tasks.Select(async task =>
            {
                try
                {
                    await task;
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }));

        Assert.Equal(1, executions);
        Assert.All(
            exceptions,
            exception => Assert.True(exception));
    }

    [Fact]
    public async Task SynchronousFailure_AllowsNewExecution()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        Task<int> Operation()
        {
            var execution = Interlocked.Increment(ref executions);

            if (execution == 1)
            {
                throw new InvalidOperationException(
                    "Synchronous failure");
            }

            return Task.FromResult(42);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => singleFlight.RunAsync("same-key", Operation));

        var result = await singleFlight.RunAsync(
            "same-key",
            Operation);

        Assert.Equal(2, executions);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ConsumerCancellation_DoesNotCancelSharedOperation()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation()
        {
            Interlocked.Increment(ref executions);

            await Task.Delay(500);

            return 42;
        }

        using var cancellationTokenSource =
            new CancellationTokenSource(100);

        var cancelledTask = singleFlight.RunAsync(
            "same-key",
            Operation,
            cancellationTokenSource.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledTask);

        var result = await singleFlight.RunAsync(
            "same-key",
            Operation);

        Assert.Equal(42, result);
        Assert.Equal(1, executions);
    }
}
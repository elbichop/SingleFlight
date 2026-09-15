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

    // New tests for CancellationToken semantics
    [Fact]
    public async Task CallerCancel_DoesNotCancelSharedOperation_OtherCallersReceiveResult()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        var opStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opContinue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Operation(CancellationToken ct)
        {
            Interlocked.Increment(ref executions);

            opStarted.SetResult();

            return await opContinue.Task.ConfigureAwait(false);
        }

        using var ctsA = new CancellationTokenSource();

        var taskA = singleFlight.RunAsync("same-key", Operation, ctsA.Token);

        await opStarted.Task;

        var taskB = singleFlight.RunAsync("same-key", Operation);

        // Cancel A's wait
        ctsA.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);

        // Operation should still be running and B should still await result
        Assert.False(taskB.IsCompleted);

        // Complete operation
        opContinue.SetResult(42);

        var resultB = await taskB;

        Assert.Equal(42, resultB);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task MultipleCallersCancel_OthersStillReceiveResult_OperationRunsOnce()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        var opStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opContinue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Operation(CancellationToken ct)
        {
            Interlocked.Increment(ref executions);

            opStarted.SetResult();

            return await opContinue.Task.ConfigureAwait(false);
        }

        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        var taskA = singleFlight.RunAsync("same-key", Operation, ctsA.Token);

        await opStarted.Task;

        var taskB = singleFlight.RunAsync("same-key", Operation, ctsB.Token);
        var taskC = singleFlight.RunAsync("same-key", Operation);

        // Cancel A and B
        ctsA.Cancel();
        ctsB.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);

        // C should still await
        Assert.False(taskC.IsCompleted);

        // Complete operation
        opContinue.SetResult(99);

        var resultC = await taskC;

        Assert.Equal(99, resultC);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancel_OperationTokenRemainsUncancelled()
    {
        var singleFlight = new SingleFlightExecutor<int>();

        var opStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opContinue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var observed = new List<bool>();

        async Task<int> Operation(CancellationToken opToken)
        {
            // record before wait
            observed.Add(opToken.IsCancellationRequested);

            opStarted.SetResult();

            // wait until test cancels caller
            var result = await opContinue.Task.ConfigureAwait(false);

            // record after continue
            observed.Add(opToken.IsCancellationRequested);

            return result;
        }

        using var ctsA = new CancellationTokenSource();

        var taskA = singleFlight.RunAsync("same-key", Operation, ctsA.Token);

        await opStarted.Task;

        var taskB = singleFlight.RunAsync("same-key", Operation);

        // Cancel caller A
        ctsA.Cancel();

        // allow operation to finish
        opContinue.SetResult(7);

        // Wait for B to receive result
        var resultB = await taskB;

        Assert.Equal(7, resultB);

        // Operation token must not have been cancelled by caller cancellation
        Assert.All(observed, flag => Assert.False(flag));
    }
}

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SingleFlight;
using Xunit;

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

    // Observability tests
    [Fact]
    public async Task Observer_ReceivesStartAndCompleted_WithCorrectCounts_Success()
    {
        var observer = new TestObserver();

        var singleFlight = new SingleFlightExecutor<int>(observer);

        var opStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opContinue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Operation(CancellationToken ct)
        {
            opStarted.SetResult();
            return await opContinue.Task.ConfigureAwait(false);
        }

        var task1 = singleFlight.RunAsync("k1", Operation);

        await opStarted.Task;

        var tasks = Enumerable.Range(0, 9).Select(_ => singleFlight.RunAsync("k1", Operation)).ToArray();

        // finish operation
        opContinue.SetResult(7);

        var results = await Task.WhenAll(tasks.Concat(new[] { task1 }));

        Assert.All(results, r => Assert.Equal(7, r));

        // Observer must have seen start and completed exactly once
        Assert.Equal(1, observer.Started.Count);
        Assert.Equal(1, observer.Completed.Count);

        var completed = observer.Completed.Single();
        Assert.Equal("k1", completed.Key);
        Assert.Equal(10, completed.CallerCount);
        Assert.Equal(OperationOutcome.Success, completed.Outcome);
    }

    [Fact]
    public async Task Observer_Outcome_FaultedAndCanceled()
    {
        var observer = new TestObserver();
        var singleFlight = new SingleFlightExecutor<int>(observer);

        async Task<int> FailOp(CancellationToken ct)
        {
            await Task.Delay(10);
            throw new InvalidOperationException("boom");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => singleFlight.RunAsync("kf", FailOp));

        Assert.Equal(1, observer.Completed.Count);
        Assert.Equal(OperationOutcome.Faulted, observer.Completed.Single().Outcome);

        // canceled operation (operation itself cancels)
        var obs2 = new TestObserver();
        var sf2 = new SingleFlightExecutor<int>(obs2);

        async Task<int> CancelOp(CancellationToken ct)
        {
            await Task.Delay(10);
            throw new OperationCanceledException();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sf2.RunAsync("kc", CancelOp));
        Assert.Equal(OperationOutcome.Canceled, obs2.Completed.Single().Outcome);
    }

    [Fact]
    public async Task Observer_Isolated_FromExceptions()
    {
        var observer = new ThrowingObserver();
        var singleFlight = new SingleFlightExecutor<int>(observer);

        async Task<int> Operation(CancellationToken ct)
        {
            await Task.Delay(10);
            return 1;
        }

        // Should not throw despite observer throwing
        var result = await singleFlight.RunAsync("kex", Operation);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Observer_ConcurrentCallers_CountsCorrectly()
    {
        var observer = new TestObserver();
        var singleFlight = new SingleFlightExecutor<int>(observer);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Operation(CancellationToken ct)
        {
            return await tcs.Task.ConfigureAwait(false);
        }

        var tasks = Enumerable.Range(0, 100).Select(_ => singleFlight.RunAsync("kc2", Operation)).ToArray();

        // ensure operation started
        while (observer.Started.IsEmpty)
        {
            await Task.Delay(1);
        }

        tcs.SetResult(5);

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(5, r));

        var completed = observer.Completed.Single();
        Assert.Equal(100, completed.CallerCount);
    }

    // New cancellation tests
    [Fact]
    public async Task CallerCancel_DoesNotCancel_OperationToken_OperationContinues()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        var opStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opContinue = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Operation(CancellationToken opToken)
        {
            Interlocked.Increment(ref executions);

            opStarted.SetResult();

            // token should be independent; record its cancellation state
            var before = opToken.IsCancellationRequested;

            var result = await opContinue.Task.ConfigureAwait(false);

            var after = opToken.IsCancellationRequested;

            if (before || after) throw new Exception("operation token was cancelled by caller");

            return result;
        }

        using var ctsA = new CancellationTokenSource();

        var taskA = singleFlight.RunAsync("same-key", Operation, ctsA.Token);

        await opStarted.Task;

        var taskB = singleFlight.RunAsync("same-key", Operation);

        ctsA.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);

        Assert.False(taskB.IsCompleted);

        opContinue.SetResult(123);

        var resultB = await taskB;

        Assert.Equal(123, resultB);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task OperationCancellation_PropagatesToCallers()
    {
        var singleFlight = new SingleFlightExecutor<int>();
        var executions = 0;

        async Task<int> Operation(CancellationToken opToken)
        {
            Interlocked.Increment(ref executions);

            await Task.Delay(10);

            throw new OperationCanceledException();
        }

        var t1 = singleFlight.RunAsync("key-c", Operation);
        var t2 = singleFlight.RunAsync("key-c", Operation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);

        Assert.Equal(1, executions);
    }

    // Simple observer for tests
    private sealed class TestObserver : ISingleFlightObserver
    {
        public readonly ConcurrentQueue<OperationStartedInfo> Started = new();
        public readonly ConcurrentQueue<OperationCompletedInfo> Completed = new();

        public void OperationStarted(OperationStartedInfo info) => Started.Enqueue(info);
        public void OperationCompleted(OperationCompletedInfo info) => Completed.Enqueue(info);
    }

    private sealed class ThrowingObserver : ISingleFlightObserver
    {
        public void OperationStarted(OperationStartedInfo info) => throw new Exception("bad");
        public void OperationCompleted(OperationCompletedInfo info) => throw new Exception("bad");
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SingleFlight;

/// <summary>
/// Executes asynchronous operations using SingleFlight semantics,
/// ensuring that concurrent callers for the same key share the same operation.
/// A lightweight observability integration is optionally supported via ISingleFlightObserver.
/// </summary>
public sealed class SingleFlightExecutor<T>
{
    // Operation entry stores metadata required for observability when an
    // observer is configured. Kept internal to avoid changing public API.
    private sealed class OperationEntry
    {
        public Task<T> Task = null!;
        public TaskCompletionSource<T> Completion = null!;
        public int CallerCount;
        public long StartTimestamp;
        public int StartedNotified;
    }

    private readonly Dictionary<string, OperationEntry> _operations = new();
    private readonly Lock _lock = new();
    private readonly ISingleFlightObserver? _observer;

    /// <summary>
    /// Creates a new executor. If an observer is provided, lightweight metadata (caller counts and start timestamp)
    /// will be tracked and observer callbacks invoked when operations start and complete.
    /// </summary>
    public SingleFlightExecutor(ISingleFlightObserver? observer = null)
    {
        _observer = observer;
    }

    /// <summary>
    /// Runs the specified operation asynchronously, ensuring that concurrent callers for the same key share the same operation.
    /// </summary>
    /// <param name="key">The key identifying the operation.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public Task<T> RunAsync(
        string key,
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        OperationEntry? entry = null;
        Task<T> task;
        var created = false;

        lock (_lock)
        {
            if (_operations.TryGetValue(key, out entry))
            {
                if (_observer is not null)
                {
                    entry.CallerCount++;
                }

                task = entry.Task;
            }
            else
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                entry = new OperationEntry
                {
                    Completion = completion,
                    Task = completion.Task,
                    CallerCount = _observer is not null ? 1 : 0,
                    StartTimestamp = _observer is not null ? Stopwatch.GetTimestamp() : 0
                };

                _operations.Add(key, entry);

                task = entry.Task;

                _ = ExecuteAsync(key, operation, entry);
                created = true;
            }
        }

        // No OperationStarted notification here: the shared operation will
        // notify the observer exactly once when execution begins.

        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }

    /// <summary>
    /// Runs the specified operation asynchronously, ensuring that concurrent callers for the same key share the same operation.
    /// </summary>
    /// <param name="key">The key identifying the operation.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public Task<T> RunAsync(
        string key,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        OperationEntry? entry = null;
        Task<T> task;
        var created = false;

        lock (_lock)
        {
            if (_operations.TryGetValue(key, out entry))
            {
                if (_observer is not null)
                {
                    entry.CallerCount++;
                }

                task = entry.Task;
            }
            else
            {
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

                entry = new OperationEntry
                {
                    Completion = completion,
                    Task = completion.Task,
                    CallerCount = _observer is not null ? 1 : 0,
                    StartTimestamp = _observer is not null ? Stopwatch.GetTimestamp() : 0
                };

                _operations.Add(key, entry);

                task = entry.Task;

                _ = ExecuteAsync(key, operation, entry);
            }
        }

        // No OperationStarted notification here: the shared operation will
        // notify the observer exactly once when execution begins.

        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }

    private static TimeSpan _ElapsedFromTimestamps(long start, long end)
    {
        if (start == 0) return TimeSpan.Zero;

        var diff = end - start;
        var seconds = (double)diff / Stopwatch.Frequency;
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task ExecuteAsync(
        string key,
        Func<Task<T>> operation,
        OperationEntry entry)
    {
        Exception? operationException = null;
        var outcome = OperationOutcome.Success;

        try
        {
            var result = await operation().ConfigureAwait(false);
            entry.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            outcome = OperationOutcome.Canceled;
            entry.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            outcome = OperationOutcome.Faulted;
            operationException = ex;
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            var callers = entry.CallerCount;
            var start = entry.StartTimestamp;
            var end = (start != 0) ? Stopwatch.GetTimestamp() : 0L;

            lock (_lock)
            {
                _operations.Remove(key);
            }

            if (_observer is not null)
            {
                var duration = _ElapsedFromTimestamps(start, end);
                var completedInfo = new OperationCompletedInfo(
                    key,
                    duration,
                    callers,
                    outcome,
                    operationException);

                try { _observer.OperationCompleted(completedInfo); } catch { }
            }
        }
    }

    private async Task ExecuteAsync(
        string key,
        Func<CancellationToken, Task<T>> operation,
        OperationEntry entry)
    {
        Exception? operationException = null;
        var outcome = OperationOutcome.Success;
        // Create an independent CancellationTokenSource for the shared
        // operation. This token is NOT linked to any caller token so that
        // caller cancellation never cancels the shared operation. The
        // operation may itself observe this token and cancel.
        using var opCts = new CancellationTokenSource();

        // Notify observer that the shared operation is starting. Use a flag to
        // guarantee a single start event even in corner cases.
        if (_observer is not null && entry.StartTimestamp != 0)
        {
            if (System.Threading.Interlocked.Exchange(ref entry.StartedNotified, 1) == 0)
            {
                try { _observer.OperationStarted(new OperationStartedInfo(key)); } catch { }
            }
        }

        try
        {
            var result = await operation(opCts.Token).ConfigureAwait(false);
            entry.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            outcome = OperationOutcome.Canceled;
            entry.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            outcome = OperationOutcome.Faulted;
            operationException = ex;
            entry.Completion.TrySetException(ex);
        }
        finally
        {
            var callers = entry.CallerCount;
            var start = entry.StartTimestamp;
            var end = (start != 0) ? Stopwatch.GetTimestamp() : 0L;

            lock (_lock)
            {
                _operations.Remove(key);
            }

            if (_observer is not null)
            {
                var duration = _ElapsedFromTimestamps(start, end);
                var completedInfo = new OperationCompletedInfo(
                    key,
                    duration,
                    callers,
                    outcome,
                    operationException);

                try { _observer.OperationCompleted(completedInfo); } catch { }
            }
        }
    }
}

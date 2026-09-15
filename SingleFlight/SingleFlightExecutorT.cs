namespace SingleFlight;

/// <summary>
/// Executes asynchronous operations using SingleFlight semantics,
/// ensuring that concurrent callers for the same key share the same operation.
/// </summary>
public sealed class SingleFlightExecutor<T>
{
    private readonly Dictionary<string, Task<T>> _operations = new();
    private readonly Lock _lock = new();

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
        Task<T> task;

        lock (_lock)
        {
            if (_operations.TryGetValue(key, out task!))
            {
                return cancellationToken.CanBeCanceled
                    ? task.WaitAsync(cancellationToken)
                    : task;
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            task = completion.Task;

            _operations.Add(key, task);

            _ = ExecuteAsync(key, operation, completion);
        }

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
        Task<T> task;

        lock (_lock)
        {
            if (_operations.TryGetValue(key, out task!))
            {
                return cancellationToken.CanBeCanceled
                    ? task.WaitAsync(cancellationToken)
                    : task;
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            task = completion.Task;

            _operations.Add(key, task);

            _ = ExecuteAsync(key, operation, completion);
        }

        return cancellationToken.CanBeCanceled
            ? task.WaitAsync(cancellationToken)
            : task;
    }

    private async Task ExecuteAsync(
        string key,
        Func<Task<T>> operation,
        TaskCompletionSource<T> completion)
    {
        try
        {
            var result = await operation().ConfigureAwait(false);

            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_lock)
            {
                _operations.Remove(key);
            }
        }
    }

    private async Task ExecuteAsync(
        string key,
        Func<CancellationToken, Task<T>> operation,
        TaskCompletionSource<T> completion)
    {
        // Pass CancellationToken.None to the shared operation so that caller
        // cancellations do not cancel the operation. We avoid creating an
        // extra CancellationTokenSource here to reduce allocations.
        try
        {
            var result = await operation(CancellationToken.None).ConfigureAwait(false);

            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            lock (_lock)
            {
                _operations.Remove(key);
            }
        }
    }
}

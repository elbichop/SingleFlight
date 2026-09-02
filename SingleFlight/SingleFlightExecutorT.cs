namespace SingleFlight;

public sealed class SingleFlightExecutor<T>
{
    private readonly Dictionary<string, Task<T>> _operations = new();
    private readonly Lock _lock = new();

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

            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);

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
}
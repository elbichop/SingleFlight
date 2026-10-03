using System;
using System.Threading;

namespace SingleFlight
{
    /// <summary>
    /// Observer interface for SingleFlight events. Implement this interface to
    /// receive lightweight notifications about operations. Implementations must
    /// be robust: exceptions thrown by the observer are ignored by SingleFlight.
    /// </summary>
    public interface ISingleFlightObserver
    {
        /// <summary>
        /// Called when a shared operation is started (first caller for a key).
        /// This is a lightweight notification; it is invoked outside internal
        /// locks. Implementations must be tolerant to concurrent invocations.
        /// </summary>
        void OperationStarted(OperationStartedInfo info);

        /// <summary>
        /// Called when a shared operation completes (success, fault or cancel).
        /// </summary>
        void OperationCompleted(OperationCompletedInfo info);
    }

    /// <summary>
    /// Outcome of a shared operation.
    /// </summary>
    public enum OperationOutcome
    {
        Success,
        Faulted,
        Canceled
    }

    /// <summary>
    /// Information provided when an operation starts.
    /// </summary>
    public readonly struct OperationStartedInfo
    {
        public OperationStartedInfo(string key)
        {
            Key = key;
            StartUtc = DateTime.UtcNow;
        }

        public string Key { get; }

        /// <summary>
        /// Approximate UTC time when operation started. Only provided for
        /// informational purposes; prefer duration from completed event.
        /// </summary>
        public DateTime StartUtc { get; }
    }

    /// <summary>
    /// Information provided when an operation completes.
    /// </summary>
    public readonly struct OperationCompletedInfo
    {
        public OperationCompletedInfo(
            string key,
            TimeSpan duration,
            int callerCount,
            OperationOutcome outcome,
            Exception? exception)
        {
            Key = key;
            Duration = duration;
            CallerCount = callerCount;
            Outcome = outcome;
            Exception = exception;
        }

        public string Key { get; }
        public TimeSpan Duration { get; }
        public int CallerCount { get; }
        public OperationOutcome Outcome { get; }
        public Exception? Exception { get; }
    }
}

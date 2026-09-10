using Microsoft.Extensions.Logging;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Bounded request/retry settings for SDK-backed Graph import reads.
    /// The installed Microsoft.Graph 6.5.0 pipeline already includes Kiota's RetryHandler
    /// (3 retries by default, respecting Retry-After for transient HTTP responses). These
    /// values keep that service-directed retry path, but cap the per HTTP request deadline
    /// and the total time spent on timeout retries so one stalled page cannot monopolise the
    /// whole Graph phase.
    /// </summary>
    public sealed class GraphRequestBudgetOptions
    {
        public static readonly GraphRequestBudgetOptions GraphImportDefault = new GraphRequestBudgetOptions(
            perRequestTimeout: TimeSpan.FromMinutes(2),
            timeoutRetryCount: 2,
            timeoutRetryDelay: TimeSpan.FromSeconds(5),
            totalTimeoutRetryBudget: TimeSpan.FromMinutes(6),
            sdkRetryCount: 3,
            sdkRetryDelaySeconds: 3,
            sdkRetryTimeLimit: TimeSpan.FromMinutes(4));

        public GraphRequestBudgetOptions(
            TimeSpan perRequestTimeout,
            int timeoutRetryCount,
            TimeSpan timeoutRetryDelay,
            TimeSpan totalTimeoutRetryBudget,
            int sdkRetryCount,
            int sdkRetryDelaySeconds,
            TimeSpan sdkRetryTimeLimit)
        {
            if (perRequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perRequestTimeout));
            if (timeoutRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(timeoutRetryCount));
            if (timeoutRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeoutRetryDelay));
            if (totalTimeoutRetryBudget < perRequestTimeout) throw new ArgumentOutOfRangeException(nameof(totalTimeoutRetryBudget));
            if (sdkRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(sdkRetryCount));
            if (sdkRetryDelaySeconds < 0) throw new ArgumentOutOfRangeException(nameof(sdkRetryDelaySeconds));
            if (sdkRetryTimeLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sdkRetryTimeLimit));

            PerRequestTimeout = perRequestTimeout;
            TimeoutRetryCount = timeoutRetryCount;
            TimeoutRetryDelay = timeoutRetryDelay;
            TotalTimeoutRetryBudget = totalTimeoutRetryBudget;
            SdkRetryCount = sdkRetryCount;
            SdkRetryDelaySeconds = sdkRetryDelaySeconds;
            SdkRetryTimeLimit = sdkRetryTimeLimit;
        }

        public TimeSpan PerRequestTimeout { get; }
        public int TimeoutRetryCount { get; }
        public TimeSpan TimeoutRetryDelay { get; }
        public TimeSpan TotalTimeoutRetryBudget { get; }
        public int SdkRetryCount { get; }
        public int SdkRetryDelaySeconds { get; }
        public TimeSpan SdkRetryTimeLimit { get; }
        public int MaxAttempts => TimeoutRetryCount + 1;

        public void Log(ILogger logger)
        {
            logger?.LogInformation(
                $"Graph import SDK request budget: per-request timeout {PerRequestTimeout.TotalSeconds:N0}s, " +
                $"timeout retries {TimeoutRetryCount:N0} with {TimeoutRetryDelay.TotalSeconds:N0}s delay, " +
                $"timeout-retry budget {TotalTimeoutRetryBudget.TotalSeconds:N0}s; Kiota RetryHandler remains enabled " +
                $"for transient HTTP responses with max retry {SdkRetryCount:N0}, initial delay {SdkRetryDelaySeconds:N0}s, " +
                $"time limit {SdkRetryTimeLimit.TotalSeconds:N0}s.");
        }
    }
}

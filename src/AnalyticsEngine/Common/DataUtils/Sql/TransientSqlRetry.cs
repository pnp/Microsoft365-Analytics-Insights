using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace DataUtils.Sql
{
    /// <summary>
    /// Retries an async SQL operation when it fails with a <em>transient</em> error - a connection that the
    /// server dropped ("the connection is broken and recovery is not possible ... unrecoverable"), a timeout,
    /// a deadlock, or an Azure SQL throttling/failover code. These recover on their own, so a single blip
    /// should not throw away a whole multi-hour import cycle.
    ///
    /// Deliberately does NOT treat constraint violations (PK 2627, unique 2601, FK 547) or other logic errors
    /// as transient - retrying those just fails again, so they are surfaced immediately for batch isolation.
    /// </summary>
    public static class TransientSqlRetry
    {
        // SQL Server / Azure SQL error numbers that are safe to retry (connection-level, timeout, deadlock,
        // throttling, failover). Mirrors the set EF Core's SqlServerRetryingExecutionStrategy uses.
        private static readonly HashSet<int> TransientErrorNumbers = new HashSet<int>
        {
            -2,     // timeout expired
            20, 64, 121, 233, 615, 926, 995, 1205, // connection / deadlock / DB-unavailable
            4060, 4221,
            10053, 10054, 10060, 10928, 10929,
            40197, 40501, 40613, 41301, 41302, 41305, 41325, 41839,
            40143, 40540, 49918, 49919, 49920
        };

        // Substrings that identify a dropped/unusable connection even when we only have the message (e.g. the
        // merge path wraps the SqlException's text into a BatchSaveException). Lower-cased before matching.
        private static readonly string[] BrokenConnectionHints =
        {
            "connection is broken",
            "marked by the server as unrecoverable",
            "transport-level error",
            "existing connection was forcibly closed",
            "semaphore timeout period has expired",
            "the wait operation timed out",
            "is not currently available"
        };

        /// <summary>
        /// True if <paramref name="ex"/> (or anything in its inner-exception / aggregate tree) is a transient
        /// SQL fault worth retrying. Constraint violations and other deterministic errors return false.
        /// </summary>
        public static bool IsTransient(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AggregateException agg)
                {
                    foreach (var inner in agg.InnerExceptions)
                    {
                        if (IsTransient(inner)) return true;
                    }
                }

                if (e is TimeoutException) return true;

                if (e is SqlException sql)
                {
                    // A constraint violation anywhere in the errors collection means "don't retry".
                    foreach (SqlError err in sql.Errors)
                    {
                        if (err.Number == 2627 || err.Number == 2601 || err.Number == 547) return false;
                    }
                    foreach (SqlError err in sql.Errors)
                    {
                        if (TransientErrorNumbers.Contains(err.Number)) return true;
                    }
                }

                if (MessageLooksTransient(e.Message)) return true;
            }
            return false;
        }

        private static bool MessageLooksTransient(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            // A constraint-violation message must never be classed transient even if we only have the text.
            if (message.IndexOf("Violation of PRIMARY KEY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Violation of UNIQUE KEY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("FOREIGN KEY constraint", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            var lower = message.ToLowerInvariant();
            foreach (var hint in BrokenConnectionHints)
            {
                if (lower.Contains(hint)) return true;
            }
            return false;
        }

        /// <summary>
        /// Run <paramref name="action"/>, retrying up to <paramref name="maxAttempts"/> times (total) when it
        /// throws a transient SQL fault, with exponential backoff. A non-transient fault, or exhausting the
        /// attempts, rethrows the last exception so the caller can isolate the failed unit of work.
        /// </summary>
        public static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, int maxAttempts, ILogger logger, string operationName, TimeSpan? baseDelay = null)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (maxAttempts < 1) maxAttempts = 1;
            var delay = baseDelay ?? TimeSpan.FromSeconds(3);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    // Backoff grows 1x, 4x, 16x, ... of baseDelay so a saturated DB gets time to recover.
                    var wait = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(4, attempt - 1));
                    logger?.LogWarning($"{operationName}: transient SQL error on attempt {attempt}/{maxAttempts} ({ex.Message}). Retrying in {wait.TotalSeconds:N0}s...");
                    await Task.Delay(wait);
                }
            }
        }
    }
}

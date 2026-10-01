using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// A small key/value store for the solution's runtime state: import checkpoints (Graph delta tokens), "last run"
    /// stamps that throttle the periodic imports, the per-Team tokens behind Teams deep analytics and a short-lived
    /// cache of Azure AI Language results. Replaces Azure Cache for Redis, which used to hold exactly this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers depend on this contract:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A key that does not exist - or whose time-to-live has passed - reads as <c>null</c>. That is a
    /// confirmed miss, not an error.</description></item>
    /// <item><description>A store that cannot be reached <b>throws</b>. Callers such as the user delta-token provider
    /// must be able to tell "there is no checkpoint" (read every user) from "the checkpoint can't be read right now"
    /// (defer, rather than start a full tenant crawl).</description></item>
    /// <item><description>Writes are last-writer-wins.</description></item>
    /// </list>
    /// <para>
    /// Keys are compared ordinally (case-sensitive), as Azure Table row keys are.
    /// </para>
    /// </remarks>
    public interface IKeyValueStore
    {
        /// <summary>Where values are kept, for log lines - e.g. <c>Azure Table 'AnalyticsState', partition 'ImportSchedule'</c>.</summary>
        string Description { get; }

        /// <summary>The stored value, or <c>null</c> when the key does not exist or has expired.</summary>
        Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stores <paramref name="value"/> under <paramref name="key"/>, replacing any previous value. A <c>null</c>
        /// value deletes the key. With <paramref name="timeToLive"/>, the value reads as missing once it has elapsed.
        /// </summary>
        Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default);

        /// <summary>Deletes the key. True when a value existed and was deleted, false when there was none.</summary>
        Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>Whether a value (that has not expired) is stored under the key. Never reads the value itself.</summary>
        Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
    }
}

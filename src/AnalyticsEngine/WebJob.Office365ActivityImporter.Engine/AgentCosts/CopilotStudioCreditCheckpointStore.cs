using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Remembers which days of an unfinished Copilot Studio per-agent read were already read in full and stored, so
    /// the retry after a failure resumes rather than repeating every per-user call.
    /// </summary>
    /// <remarks>
    /// <para>The per-agent figures are rebuilt from one call per active user per day - tens of thousands of calls on
    /// a large tenant - and each day is stored as soon as it is complete. Without this, a failure late in the window
    /// sent the retry back to the first day.</para>
    /// <para>It is an <b>attempt</b> checkpoint, not a cache. It only applies to a retry of the same window (same
    /// first and last day) on the same billing connection, and it is cleared once a window completes, so the next
    /// scheduled run still re-reads every day - Microsoft restates recent days, which is why the window is re-read at
    /// all.</para>
    /// <para>Best effort throughout: a checkpoint that cannot be read means the whole window is read again, and one
    /// that cannot be written means a retry reads more than it needed to. Neither loses data.</para>
    /// </remarks>
    public class CopilotStudioCreditCheckpointStore
    {
        /// <summary>Row key prefix in the ImportSchedule partition; the connection generation follows it.</summary>
        public const string KeyPrefix = "CopilotStudioCreditsFanOutCheckpoint";

        /// <summary>A checkpoint only ever helps a retry of today's window, so it never needs to outlive two days.</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromDays(2);

        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;

        public CopilotStudioCreditCheckpointStore(IKeyValueStore store, string connectionVersion, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
            Key = KeyPrefix + (string.IsNullOrEmpty(connectionVersion) ? string.Empty : "_" + connectionVersion);
        }

        /// <summary>Where the checkpoint is kept. One per billing connection, so a reconnect never resumes another's read.</summary>
        public string Key { get; }

        /// <summary>
        /// The days of exactly this window that an earlier, unfinished attempt already stored. Empty when there is no
        /// checkpoint, when it belongs to another window, or when it cannot be read.
        /// </summary>
        public async Task<HashSet<DateTime>> GetCompletedDaysAsync(DateTime windowFrom, DateTime windowTo)
        {
            var none = new HashSet<DateTime>();
            try
            {
                var raw = await _store.GetStringAsync(Key).ConfigureAwait(false);
                if (string.IsNullOrEmpty(raw)) return none;

                var saved = JsonConvert.DeserializeObject<Checkpoint>(raw);
                if (saved?.CompletedDays == null
                    || saved.WindowFrom != Format(windowFrom)
                    || saved.WindowTo != Format(windowTo))
                {
                    // Another window: a new day has started (or the window was reconfigured), so this is a scheduled
                    // run that must re-read everything, not a retry.
                    return none;
                }

                var days = new HashSet<DateTime>();
                foreach (var value in saved.CompletedDays)
                {
                    if (DateTime.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        && day >= windowFrom.Date && day <= windowTo.Date)
                    {
                        days.Add(day);
                    }
                }
                return days;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Copilot Studio credits: could not read the resume checkpoint '{Key}' ({ex.Message}); "
                    + "every day in the window will be read.");
                return none;
            }
        }

        /// <summary>Records the days of this window that are now stored. Never throws.</summary>
        public async Task SaveAsync(DateTime windowFrom, DateTime windowTo, IEnumerable<DateTime> completedDays)
        {
            try
            {
                var checkpoint = new Checkpoint
                {
                    WindowFrom = Format(windowFrom),
                    WindowTo = Format(windowTo),
                    CompletedDays = completedDays.Select(Format).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList(),
                };
                await _store.SetStringAsync(Key, JsonConvert.SerializeObject(checkpoint), Lifetime).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Copilot Studio credits: could not save the resume checkpoint '{Key}' ({ex.Message}). "
                    + "Nothing is lost; a retry would just read again the days already stored.");
            }
        }

        /// <summary>Forgets the checkpoint once the window is complete. Never throws.</summary>
        public async Task ClearAsync()
        {
            try
            {
                await _store.DeleteAsync(Key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Harmless if it lingers: it only matches a retry of this same window, and expires in two days.
                _logger?.LogWarning($"Copilot Studio credits: could not clear the resume checkpoint '{Key}' ({ex.Message}).");
            }
        }

        private const string DayFormat = "yyyy-MM-dd";

        private static string Format(DateTime day) => day.Date.ToString(DayFormat, CultureInfo.InvariantCulture);

        private sealed class Checkpoint
        {
            public string WindowFrom { get; set; }
            public string WindowTo { get; set; }
            public List<string> CompletedDays { get; set; }
        }
    }
}

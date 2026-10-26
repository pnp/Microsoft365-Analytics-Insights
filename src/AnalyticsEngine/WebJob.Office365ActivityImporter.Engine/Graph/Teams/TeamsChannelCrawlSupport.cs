using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>What one Team's channel crawl did, for the crawl's summary line. Used by one Team's crawl at a time.</summary>
    public sealed class TeamsChannelCrawlStats
    {
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Channels the budget picked for this crawl (all of them when there is no budget).</summary>
        public int ChannelsSelected { get; internal set; }

        /// <summary>Channels whose messages were read to the end.</summary>
        public int ChannelsRead { get; internal set; }

        /// <summary>Channels this person's token can't read (a private channel they are not in, or one deleted since it was listed).</summary>
        public int ChannelsSkipped { get; internal set; }

        /// <summary>Channels whose read failed. See <see cref="TopFailure"/>.</summary>
        public int ChannelsFailed { get; internal set; }

        /// <summary>Channels the per-cycle budget left for a later crawl.</summary>
        public int ChannelsDeferred { get; internal set; }

        /// <summary>Reads that ended with a delta token to commit once SQL has the data.</summary>
        public int TokensToCommit { get; internal set; }

        /// <summary>Reads that ended with no token to commit, so the stored one is left as it was: a paging cap, or none offered.</summary>
        public int TokensWithheld { get; internal set; }

        /// <summary>Stored delta tokens Graph rejected, replaced by a full read of the channel.</summary>
        public int TokensReset { get; internal set; }

        /// <summary>The Team's crawl was stopped early: throttling, a token problem, or several channels failing in a row.</summary>
        public bool Stopped { get; internal set; }

        /// <summary>Failure kinds and how many channels failed that way, e.g. <c>HTTP 400 UnknownError</c>.</summary>
        public IReadOnlyDictionary<string, int> Failures => _failures;

        public string TopFailure => _failures.Count == 0
            ? null
            : _failures.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).First().Key;

        internal void RecordFailure(Exception ex)
        {
            ChannelsFailed++;
            var kind = FailureClassification.Describe(ex);
            _failures[kind] = _failures.TryGetValue(kind, out var count) ? count + 1 : 1;
        }
    }

    /// <summary>
    /// How many times in a row each of a Team's channels has failed to read, kept in the <see cref="StatePartitions.TeamsChannels"/>
    /// partition as one small row per Team holding only the channels that are failing. The count only decides
    /// when the budget's scheduling cursor moves past a channel (<see cref="MovePastAfterFailures"/>) and what the
    /// <c>TeamsChannelReadFailed</c> line says: it never touches a channel's delta token, so a failing channel still resumes from
    /// where it was once Graph answers again.
    /// </summary>
    /// <remarks>
    /// Fail-open: a store that can't be read or written costs only the counts (every failure then reads as the first), never the
    /// crawl. One read per Team per crawl, and one write only when a count changed.
    /// </remarks>
    public sealed class TeamsChannelFailureLedger
    {
        /// <summary>After this many failed reads in a row the budget's cursor moves past the channel (its delta token stays).</summary>
        public const int MovePastAfterFailures = 3;

        /// <summary>Bounds the row. A Team with more failing channels than this has a bigger problem than their counts.</summary>
        internal const int MaxTrackedChannels = 2000;

        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;
        private Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private string _key;
        private bool _dirty;
        private bool _usable;

        public TeamsChannelFailureLedger(IKeyValueStore store, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>The row key for a Team. A new kind of row in the partition: it can't collide with a channel's <c>team|channel</c> or <c>budget|team</c> key.</summary>
        internal static string KeyFor(string teamId) => "failures|" + teamId;

        public async Task LoadAsync(string teamId, IEnumerable<string> currentChannelIds)
        {
            _key = KeyFor(teamId);
            _counts = new Dictionary<string, int>(StringComparer.Ordinal);
            _dirty = false;
            _usable = true;
            try
            {
                var raw = await _store.GetStringAsync(_key);
                if (!string.IsNullOrEmpty(raw))
                {
                    _counts = JsonConvert.DeserializeObject<Dictionary<string, int>>(raw) ?? _counts;
                }
            }
            catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                _usable = false;
                _logger.LogWarning("TeamsChannelFailureCountsUnavailable: stage=load; failure={Failure}. Consecutive failure counts are not kept this crawl.", FailureClassification.Describe(ex));
                return;
            }

            // A deleted channel's count would otherwise stay for ever.
            var current = new HashSet<string>(currentChannelIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            foreach (var stale in _counts.Keys.Where(id => !current.Contains(id)).ToList())
            {
                _counts.Remove(stale);
                _dirty = true;
            }
        }

        /// <summary>How many times in a row the channel's read has failed, as last recorded (0 when it hasn't).</summary>
        public int ConsecutiveFailures(string channelId) => _usable && _counts.TryGetValue(channelId, out var count) ? count : 0;

        /// <summary>Records a failed read and returns how many in a row the channel has now failed.</summary>
        public int RecordFailure(string channelId)
        {
            if (!_usable)
            {
                return 1;
            }

            if (!_counts.TryGetValue(channelId, out var count))
            {
                if (_counts.Count >= MaxTrackedChannels)
                {
                    return 1;
                }
                count = 0;
            }

            _counts[channelId] = ++count;
            _dirty = true;
            return count;
        }

        public void RecordSuccess(string channelId)
        {
            if (_usable && _counts.Remove(channelId))
            {
                _dirty = true;
            }
        }

        public async Task SaveAsync()
        {
            if (!_usable || !_dirty)
            {
                return;
            }

            try
            {
                if (_counts.Count == 0)
                {
                    await _store.DeleteAsync(_key);
                }
                else
                {
                    await _store.SetStringAsync(_key, JsonConvert.SerializeObject(_counts));
                }
                _dirty = false;
            }
            catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                _logger.LogWarning("TeamsChannelFailureCountsUnavailable: stage=save; failure={Failure}. This crawl's failure counts are lost.", FailureClassification.Describe(ex));
            }
        }
    }
}

using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Stops one mailbox that can never be saved from wedging the sent-email import for ever. A mailbox whose save or checkpoint
    /// write has failed <see cref="QuarantineAfterConsecutiveFailures"/> times in a row is quarantined for
    /// <see cref="QuarantineFor"/>: it isn't read, its failure no longer fails the section (which skips every Graph section after
    /// it, such as the Copilot interaction history), and an Error names it. Its checkpoint is left exactly as it was, so when
    /// the quarantine ends it resumes where it stopped.
    /// </summary>
    /// <remarks>
    /// State is one small row (<see cref="StateKey"/>) in the <see cref="StatePartitions.SentEmails"/> partition holding only the
    /// mailboxes that are failing - by user id, never UPN - so it costs one read and at most one write per run, however many
    /// mailboxes there are. At most <see cref="MaxTrackedMailboxes"/> are tracked; more failing mailboxes than that is a systemic
    /// fault, not a poisoned mailbox. Fail-open: a store that can't be read or written costs only the quarantine.
    /// </remarks>
    public sealed class SentEmailMailboxQuarantine
    {
        public const int QuarantineAfterConsecutiveFailures = 3;

        public static readonly TimeSpan QuarantineFor = TimeSpan.FromHours(24);

        internal const int MaxTrackedMailboxes = 2000;

        /// <summary>The row key. Distinct from the delta-token keys (<c>SentEmails-&lt;upn&gt;</c>) and the no-mailbox list's.</summary>
        internal const string StateKey = "SentEmailMailboxQuarantine";

        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();
        private Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private bool _dirty;
        private bool _usable = true;

        public SentEmailMailboxQuarantine(IKeyValueStore store, ILogger logger, Func<DateTime> utcNow = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task LoadAsync()
        {
            try
            {
                var raw = await _store.GetStringAsync(StateKey);
                lock (_lock)
                {
                    _entries = string.IsNullOrEmpty(raw)
                        ? new Dictionary<int, Entry>()
                        : JsonConvert.DeserializeObject<Dictionary<int, Entry>>(raw) ?? new Dictionary<int, Entry>();
                    _dirty = false;
                    _usable = true;
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _usable = false;
                }
                _logger.LogWarning("SentEmailQuarantineUnavailable: stage=load; failure={Failure}. No mailbox is quarantined this run.", FailureClassification.Describe(ex));
            }
        }

        public bool IsQuarantined(int userId)
        {
            lock (_lock)
            {
                return _usable && _entries.TryGetValue(userId, out var entry) && entry.UntilUtc > _utcNow();
            }
        }

        public void RecordSuccess(int userId)
        {
            lock (_lock)
            {
                if (_usable && _entries.Remove(userId))
                {
                    _dirty = true;
                }
            }
        }

        /// <summary>Counts a failed save and says whether the mailbox is now quarantined.</summary>
        public QuarantineDecision RecordFailure(int userId, Exception failure)
        {
            lock (_lock)
            {
                if (!_usable)
                {
                    return new QuarantineDecision(1, false, null);
                }

                if (!_entries.TryGetValue(userId, out var entry))
                {
                    if (_entries.Count >= MaxTrackedMailboxes)
                    {
                        return new QuarantineDecision(1, false, null);
                    }

                    entry = new Entry();
                    _entries[userId] = entry;
                }

                entry.Failures++;
                entry.Failure = FailureClassification.Describe(failure);
                _dirty = true;
                if (entry.Failures < QuarantineAfterConsecutiveFailures)
                {
                    return new QuarantineDecision(entry.Failures, false, null);
                }

                entry.UntilUtc = _utcNow() + QuarantineFor;
                return new QuarantineDecision(entry.Failures, true, entry.UntilUtc);
            }
        }

        /// <summary>How many mailboxes are in quarantine now.</summary>
        public int QuarantinedCount()
        {
            lock (_lock)
            {
                var now = _utcNow();
                return _usable ? _entries.Values.Count(e => e.UntilUtc > now) : 0;
            }
        }

        public async Task SaveAsync()
        {
            string payload;
            lock (_lock)
            {
                if (!_usable || !_dirty)
                {
                    return;
                }

                // A quarantine that has ended and whose mailbox has not failed again is dropped with the next write, but an entry
                // that is still counting (not yet quarantined) is kept.
                payload = _entries.Count == 0 ? null : JsonConvert.SerializeObject(_entries);
            }

            try
            {
                await _store.SetStringAsync(StateKey, payload);
                lock (_lock)
                {
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SentEmailQuarantineUnavailable: stage=save; failure={Failure}. This run's quarantine changes are lost.", FailureClassification.Describe(ex));
            }
        }

        private sealed class Entry
        {
            [JsonProperty("n")]
            public int Failures { get; set; }

            [JsonProperty("until")]
            public DateTime UntilUtc { get; set; }

            [JsonProperty("f")]
            public string Failure { get; set; }
        }
    }

    /// <summary>What recording a mailbox's failure decided.</summary>
    public sealed class QuarantineDecision
    {
        public QuarantineDecision(int consecutiveFailures, bool quarantined, DateTime? untilUtc)
        {
            ConsecutiveFailures = consecutiveFailures;
            Quarantined = quarantined;
            UntilUtc = untilUtc;
        }

        public int ConsecutiveFailures { get; }

        public bool Quarantined { get; }

        public DateTime? UntilUtc { get; }
    }
}

using Common.Entities.Calls;
using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// What the importer remembers about the call-records subscription it created. Graph never returns a subscription's
    /// clientState, so this is the only way to tell a subscription created with the current
    /// <see cref="CallWebhookClientState"/> from one created before it - or before the client secret was rotated.
    /// Holds the <see cref="CallWebhookClientState.Fingerprint"/>, never the clientState itself.
    /// </summary>
    public sealed class CallSubscriptionRecord
    {
        [JsonProperty("subscriptionId")]
        public string SubscriptionId { get; set; }

        [JsonProperty("clientStateFingerprint")]
        public string ClientStateFingerprint { get; set; }

        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Where <see cref="CallWebhook"/> keeps its <see cref="CallSubscriptionRecord"/>, per notification URL.</summary>
    public interface ICallSubscriptionRecordStore
    {
        /// <summary>The record, or <c>null</c> when there is none. Throws when it can't be read.</summary>
        Task<CallSubscriptionRecord> GetAsync(Uri notificationUrl);

        Task SaveAsync(Uri notificationUrl, CallSubscriptionRecord record);

        /// <summary>
        /// When this process first found the replaced legacy subscription(s) still in place, or <c>null</c>. For this
        /// process only, deliberately: an upgrade restarts the importer, and the wait before deleting them must start
        /// again then - see <see cref="CallWebhook.LegacyGracePeriod"/>.
        /// </summary>
        Task<DateTime?> GetLegacySeenAsync(Uri notificationUrl);

        Task SetLegacySeenAsync(Uri notificationUrl, DateTime? seenUtc);
    }

    /// <summary>
    /// The runtime state table (partition <see cref="Partition"/>, keyed by notification URL), plus a copy of what this
    /// process saved, which takes precedence.
    /// </summary>
    /// <remarks>
    /// The copy is what stops a table that can be read but not written from making the importer replace the subscription
    /// every cycle: a failed save still leaves this process knowing which subscription it created, so at worst the
    /// subscription is replaced once more after a restart. Without a Storage account the copy is all there is, and the
    /// subscription is replaced once each time the importer starts.
    /// </remarks>
    public sealed class CallSubscriptionRecordStore : ICallSubscriptionRecordStore
    {
        /// <summary>Partition of the runtime state table. Part of the stored data's address: don't rename it.</summary>
        public const string Partition = "CallsWebhook";

        private readonly IKeyValueStore _durable;
        private readonly IKeyValueStore _process;

        /// <param name="durable">The runtime state table, or <c>null</c> when no Storage account is configured.</param>
        /// <param name="process">Must live as long as the process - <c>ProgramTasks</c> is rebuilt every cycle.</param>
        public CallSubscriptionRecordStore(IKeyValueStore durable, IKeyValueStore process)
        {
            _durable = durable;
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        public async Task<CallSubscriptionRecord> GetAsync(Uri notificationUrl)
        {
            var key = Key(notificationUrl);

            // This process's copy first: it is written before the table, so it is never older - and the importer WebJob is a
            // singleton, so nothing else writes the table meanwhile.
            var json = await _process.GetStringAsync(key).ConfigureAwait(false);
            if (json == null && _durable != null)
            {
                json = await _durable.GetStringAsync(key).ConfigureAwait(false);
            }

            if (json == null) return null;
            try
            {
                return JsonConvert.DeserializeObject<CallSubscriptionRecord>(json);
            }
            catch (JsonException)
            {
                // Unreadable is treated as absent: the subscription is replaced once and the record rewritten.
                return null;
            }
        }

        public async Task SaveAsync(Uri notificationUrl, CallSubscriptionRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var key = Key(notificationUrl);
            var json = JsonConvert.SerializeObject(record);
            await _process.SetStringAsync(key, json).ConfigureAwait(false);
            if (_durable != null)
            {
                await _durable.SetStringAsync(key, json).ConfigureAwait(false);
            }
        }

        public async Task<DateTime?> GetLegacySeenAsync(Uri notificationUrl)
        {
            var value = await _process.GetStringAsync(LegacySeenKey(notificationUrl)).ConfigureAwait(false);
            return value == null
                ? (DateTime?)null
                : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        public Task SetLegacySeenAsync(Uri notificationUrl, DateTime? seenUtc)
        {
            return _process.SetStringAsync(LegacySeenKey(notificationUrl), seenUtc?.ToString("o", CultureInfo.InvariantCulture));
        }

        private static string LegacySeenKey(Uri notificationUrl) => "LegacySeen|" + Key(notificationUrl);

        private static string Key(Uri notificationUrl)
        {
            if (notificationUrl == null) throw new ArgumentNullException(nameof(notificationUrl));
            return notificationUrl.ToString();
        }
    }
}

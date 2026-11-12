using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.Calls
{
    /// <summary>The stored request and the version it was read at, for a write that must not overwrite someone else's.</summary>
    public sealed class CallDeadLetterRequestSnapshot
    {
        public CallDeadLetterRequestSnapshot(CallDeadLetterRequest request, string versionToken)
        {
            Request = request;
            VersionToken = versionToken;
        }

        /// <summary>The latest request; null when none has been recorded (or the row can't be read as one).</summary>
        public CallDeadLetterRequest Request { get; }

        /// <summary>The row's version as read; null when there is no row at all.</summary>
        public string VersionToken { get; }
    }

    /// <summary>The store could not be reached, or refused the operation.</summary>
    public sealed class CallDeadLetterStateUnavailableException : Exception
    {
        public CallDeadLetterStateUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Where the latest <see cref="CallDeadLetterRequest"/> is kept: one key, <see cref="Key"/>, in the
    /// <see cref="StatePartitions.CallDeadLetters"/> partition of the runtime state table. The web app writes a new request
    /// there; the importer claims it and records its result there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write is a compare-and-set on the row's version where the store offers one (both the Azure Table store and the
    /// in-memory one do): two administrators asking at once, or two importer instances claiming the same request, can't
    /// both succeed. Where a store has no versions, a write simply replaces the row - which the importer tolerates, as a
    /// replay is at-least-once and saving a call replaces any copy already saved.
    /// </para>
    /// <para>
    /// The <see cref="IKeyValueStore"/> contract holds: a missing key reads as no request, an unreachable store throws
    /// (wrapped in <see cref="CallDeadLetterStateUnavailableException"/>). Without a Storage connection string,
    /// <see cref="ForConfig"/> falls back to a process-wide in-memory store, created once - and says so with
    /// <see cref="IsDurable"/>, because a request kept in the web app's memory can never reach the importer.
    /// </para>
    /// </remarks>
    public sealed class CallDeadLetterRequestStore
    {
        /// <summary>The row key of the latest request. Part of the stored data's address: don't rename it.</summary>
        public const string Key = "LatestRequest";

        private static readonly InMemoryKeyValueStore InMemoryFallback = new InMemoryKeyValueStore();

        private readonly IKeyValueStore _values;
        private readonly IConditionalKeyValueStore _conditional;

        public CallDeadLetterRequestStore(IKeyValueStore values, bool isDurable)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            _conditional = values as IConditionalKeyValueStore;
            IsDurable = isDurable;
        }

        /// <summary>
        /// The durable store for this deployment's runtime state table, or - without a Storage connection string - the
        /// process's in-memory fallback, with <see cref="IsDurable"/> false.
        /// </summary>
        public static CallDeadLetterRequestStore ForConfig(AppConfig config, ILogger logger = null)
        {
            var durable = StateStore.TryOpen(config, StatePartitions.CallDeadLetters, logger);
            return new CallDeadLetterRequestStore(durable ?? InMemoryFallback, isDurable: durable != null);
        }

        /// <summary>
        /// Whether requests are kept in the runtime state table, where the web app and the importer both see them. False for
        /// the in-memory fallback, which only this process sees.
        /// </summary>
        public bool IsDurable { get; }

        /// <summary>Where requests are kept, for log lines.</summary>
        public string Description => _values.Description;

        /// <summary>The latest request and its version. Throws <see cref="CallDeadLetterStateUnavailableException"/> when the store can't be read.</summary>
        public async Task<CallDeadLetterRequestSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_conditional != null)
                {
                    var versioned = await _conditional.GetVersionedAsync(Key, cancellationToken).ConfigureAwait(false);
                    return new CallDeadLetterRequestSnapshot(Deserialize(versioned.Value), versioned.VersionToken);
                }
                var value = await _values.GetStringAsync(Key, cancellationToken).ConfigureAwait(false);
                return new CallDeadLetterRequestSnapshot(Deserialize(value), null);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                throw new CallDeadLetterStateUnavailableException($"Couldn't read the Teams calls dead-letter request from {_values.Description}.", ex);
            }
        }

        /// <summary>
        /// Stores <paramref name="request"/> in place of the row read at <paramref name="expectedVersionToken"/> (null: only
        /// if there is no row). False, writing nothing, when someone else changed the row since. Throws
        /// <see cref="CallDeadLetterStateUnavailableException"/> when the store can't be written.
        /// </summary>
        public async Task<bool> TryReplaceAsync(CallDeadLetterRequest request, string expectedVersionToken, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var json = JsonConvert.SerializeObject(request);
            try
            {
                if (_conditional != null)
                {
                    return await _conditional.TrySetStringAsync(Key, json, expectedVersionToken, cancellationToken).ConfigureAwait(false);
                }
                await _values.SetStringAsync(Key, json, null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                throw new CallDeadLetterStateUnavailableException($"Couldn't write the Teams calls dead-letter request to {_values.Description}.", ex);
            }
        }

        /// <summary>A row that isn't a request - written by hand, or damaged - reads as none, so a new request can replace it.</summary>
        private static CallDeadLetterRequest Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var request = JsonConvert.DeserializeObject<CallDeadLetterRequest>(json);
                return request == null || string.IsNullOrEmpty(request.Id) || CallDeadLetterActions.Parse(request.Action) == null ? null : request;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

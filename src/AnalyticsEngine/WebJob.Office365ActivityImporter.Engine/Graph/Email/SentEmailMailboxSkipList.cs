using Common.Entities.Redis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// The set of user principal names known to have no Exchange Online mailbox, plus when that set was
    /// last rebuilt from a full sweep of every user.
    /// </summary>
    public class MailboxSkipList
    {
        /// <summary>When the set was last rebuilt by checking every user. Null = never swept.</summary>
        [JsonProperty("generatedUtc")]
        public DateTime? GeneratedUtc { get; set; }

        [JsonProperty("upns")]
        public List<string> Upns { get; set; } = new List<string>();

        [JsonIgnore]
        public HashSet<string> UpnSet => new HashSet<string>(Upns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        public static MailboxSkipList Empty() => new MailboxSkipList();
    }

    /// <summary>
    /// Stores the "these users have no mailbox" negative cache for the sent-email importer.
    ///
    /// Deliberately a <b>single</b> key holding the whole set rather than one key per user: the importer
    /// runs every cycle against every user, so per-user lookups would be one round trip per user per
    /// cycle (200,000 round trips at the tenant scale this solution targets). One read at the start of a
    /// run and one write at the end is O(1) regardless of tenant size.
    /// </summary>
    public interface ISentEmailMailboxSkipList
    {
        Task<MailboxSkipList> LoadAsync();
        Task SaveAsync(MailboxSkipList skipList);
    }

    /// <summary>
    /// In-memory skip list, used when Redis isn't configured. The cache still works for the lifetime of
    /// the WebJob process (which runs many import cycles), and resets on restart - so a restart is itself
    /// a way to force an immediate re-check of every mailbox.
    /// </summary>
    public class InMemorySentEmailMailboxSkipList : ISentEmailMailboxSkipList
    {
        private MailboxSkipList _current = MailboxSkipList.Empty();
        private readonly object _lock = new object();

        public Task<MailboxSkipList> LoadAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(new MailboxSkipList
                {
                    GeneratedUtc = _current.GeneratedUtc,
                    Upns = _current.Upns.ToList(),
                });
            }
        }

        public Task SaveAsync(MailboxSkipList skipList)
        {
            lock (_lock)
            {
                _current = new MailboxSkipList
                {
                    GeneratedUtc = skipList.GeneratedUtc,
                    Upns = (skipList.Upns ?? new List<string>()).ToList(),
                };
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Redis-backed skip list, so the negative cache survives WebJob restarts and is shared between the
    /// importer instances. Reads and writes are <b>fail-open</b>: a Redis outage yields an empty skip list
    /// (so every mailbox is checked, exactly like the legacy behaviour) rather than failing the import.
    /// </summary>
    public class RedisSentEmailMailboxSkipList : ISentEmailMailboxSkipList
    {
        internal const string CacheKey = "SentEmailNoMailboxUsers";

        private readonly CacheConnectionManager _cacheConnectionManager;
        private readonly ILogger _logger;

        public RedisSentEmailMailboxSkipList(string redisConnectionString, ILogger logger, string tenantId = null, string clientId = null, string clientSecret = null)
        {
            _cacheConnectionManager = CacheConnectionManager.GetConnectionManager(redisConnectionString, tenantId: tenantId, clientId: clientId, clientSecret: clientSecret);
            _logger = logger;
        }

        public async Task<MailboxSkipList> LoadAsync()
        {
            try
            {
                var raw = await _cacheConnectionManager.GetString(CacheKey);
                if (string.IsNullOrEmpty(raw))
                    return MailboxSkipList.Empty();

                return JsonConvert.DeserializeObject<MailboxSkipList>(raw) ?? MailboxSkipList.Empty();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Sent emails: could not read the no-mailbox skip list from Redis ({ex.Message}); " +
                    "every mailbox will be checked this cycle.");
                return MailboxSkipList.Empty();
            }
        }

        public async Task SaveAsync(MailboxSkipList skipList)
        {
            try
            {
                await _cacheConnectionManager.SetString(CacheKey, JsonConvert.SerializeObject(skipList));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Sent emails: could not save the no-mailbox skip list to Redis ({ex.Message}); " +
                    "mailbox-less users will be re-checked next cycle.");
            }
        }
    }
}

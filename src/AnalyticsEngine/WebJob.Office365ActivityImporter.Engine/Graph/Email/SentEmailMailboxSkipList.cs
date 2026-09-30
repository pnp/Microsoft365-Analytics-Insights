using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// Deliberately a whole set read once at the start of a run and written once at the end, never one lookup
    /// per user: the importer runs every cycle against every user, so per-user lookups would be one round trip
    /// per user per cycle (200,000 round trips at the tenant scale this solution targets).
    /// </summary>
    public interface ISentEmailMailboxSkipList
    {
        Task<MailboxSkipList> LoadAsync();
        Task SaveAsync(MailboxSkipList skipList);
    }

    /// <summary>
    /// In-memory skip list, used when no Storage connection string is configured. The cache still works for the lifetime
    /// of the WebJob process (which runs many import cycles), and resets on restart - so a restart is itself
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
    /// Durable skip list in the runtime state store (the <see cref="StatePartitions.SentEmails"/> partition of the
    /// state table), so the negative cache survives WebJob restarts. Reads and writes are <b>fail-open</b>: a storage
    /// outage yields an empty skip list (so every mailbox is checked, exactly like the legacy behaviour) rather than
    /// failing the import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table row holds at most 1 MiB, and on a large tenant the list can outgrow that even compressed: guests and
    /// unlicensed accounts have no mailbox, and around 125,000 UPNs is already too many for one row. So the list is a
    /// header row (<see cref="CacheKey"/>: when it was last rebuilt and how many pages hold it) plus pages of at most
    /// <see cref="MaxUpnsPerPage"/> UPNs (<see cref="PageKey"/>), sorted so that neighbouring UPNs share prefixes and
    /// compress well. At 200,000 mailbox-less users that is 20 page reads per cycle, not 200,000.
    /// </para>
    /// <para>
    /// Pages are written before the header and pages a shorter list no longer needs are deleted after it, so a
    /// failed save leaves the previous header pointing at pages that still exist. A page that cannot be read makes
    /// the whole list read as empty, like any other read failure.
    /// </para>
    /// </remarks>
    public class PersistedSentEmailMailboxSkipList : ISentEmailMailboxSkipList
    {
        /// <summary>The header row: when the list was last rebuilt by a full sweep, and how many pages hold it.</summary>
        internal const string CacheKey = "SentEmailNoMailboxUsers";

        /// <summary>
        /// UPNs per page. Even at the longest UPN Entra allows (113 characters) a page is about 1.2 MB of JSON, which
        /// fits one row compressed; a typical page is well under 100 KB.
        /// </summary>
        internal const int MaxUpnsPerPage = 10000;

        /// <summary>How many pages are read at once.</summary>
        private const int PageReadConcurrency = 16;

        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;

        public PersistedSentEmailMailboxSkipList(IKeyValueStore store, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        /// <summary>The key of page <paramref name="page"/> (zero-based).</summary>
        internal static string PageKey(int page) => CacheKey + ":" + page.ToString(CultureInfo.InvariantCulture);

        public async Task<MailboxSkipList> LoadAsync()
        {
            try
            {
                var header = ParseHeader(await _store.GetStringAsync(CacheKey));
                if (header == null)
                    return MailboxSkipList.Empty();

                var upns = new List<string>(header.Count);
                for (var first = 0; first < header.Pages; first += PageReadConcurrency)
                {
                    var batch = Enumerable.Range(first, Math.Min(PageReadConcurrency, header.Pages - first)).ToList();
                    var pages = await Task.WhenAll(batch.Select(p => _store.GetStringAsync(PageKey(p))));
                    for (var i = 0; i < pages.Length; i++)
                    {
                        if (pages[i] == null)
                            throw new InvalidDataException($"page {batch[i] + 1} of {header.Pages} is missing");

                        upns.AddRange(JsonConvert.DeserializeObject<List<string>>(pages[i]) ?? new List<string>());
                    }
                }

                return new MailboxSkipList { GeneratedUtc = header.GeneratedUtc, Upns = upns };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Sent emails: could not read the no-mailbox skip list from the state store ({ex.Message}); " +
                    "every mailbox will be checked this cycle.");
                return MailboxSkipList.Empty();
            }
        }

        public async Task SaveAsync(MailboxSkipList skipList)
        {
            try
            {
                var upns = new HashSet<string>((skipList?.Upns ?? new List<string>()).Where(u => !string.IsNullOrEmpty(u)), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                upns.Sort(StringComparer.OrdinalIgnoreCase);
                var pageCount = (upns.Count + MaxUpnsPerPage - 1) / MaxUpnsPerPage;

                // Read before anything is overwritten, so the pages a shorter list no longer needs can be removed.
                var previousPageCount = ParseHeaderLeniently(await _store.GetStringAsync(CacheKey))?.Pages ?? 0;

                for (var p = 0; p < pageCount; p++)
                {
                    var page = upns.GetRange(p * MaxUpnsPerPage, Math.Min(MaxUpnsPerPage, upns.Count - p * MaxUpnsPerPage));
                    await _store.SetStringAsync(PageKey(p), JsonConvert.SerializeObject(page));
                }

                await _store.SetStringAsync(CacheKey, JsonConvert.SerializeObject(new SkipListHeader
                {
                    GeneratedUtc = skipList?.GeneratedUtc,
                    Pages = pageCount,
                    Count = upns.Count,
                }));

                for (var p = pageCount; p < previousPageCount; p++)
                {
                    await _store.DeleteAsync(PageKey(p));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Sent emails: could not save the no-mailbox skip list to the state store ({ex.Message}); " +
                    "mailbox-less users will be re-checked next cycle.");
            }
        }

        private static SkipListHeader ParseHeader(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;

            var header = JsonConvert.DeserializeObject<SkipListHeader>(raw);
            if (header != null && (header.Pages < 0 || header.Count < 0))
                throw new InvalidDataException($"the header says {header.Pages} page(s) and {header.Count} UPN(s)");

            return header;
        }

        /// <summary>The stored header, or <c>null</c> when there is none or it cannot be understood.</summary>
        private static SkipListHeader ParseHeaderLeniently(string raw)
        {
            try
            {
                return ParseHeader(raw);
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
            {
                return null;
            }
        }

        internal sealed class SkipListHeader
        {
            [JsonProperty("generatedUtc")]
            public DateTime? GeneratedUtc { get; set; }

            [JsonProperty("pages")]
            public int Pages { get; set; }

            [JsonProperty("count")]
            public int Count { get; set; }
        }
    }
}

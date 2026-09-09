using Common.Entities;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// Reads and writes <c>dbo.users.azure_ad_id</c> - the Entra object id the Graph user import
    /// already stores for every user it sees.
    /// </summary>
    internal class SqlUserEntraIdStore : IUserEntraIdStore
    {
        /// <summary>
        /// Matches the chunk size the rest of the user pipeline uses. EF6 sends each element of a
        /// <c>Contains</c> list as its own parameter and SQL Server allows 2,100 per command, so this
        /// stays comfortably below that.
        /// </summary>
        public const int ChunkSize = 1000;

        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly int _chunkSize;

        public SqlUserEntraIdStore(IAnalyticsDbContextFactory contextFactory) : this(contextFactory, ChunkSize) { }

        public SqlUserEntraIdStore(IAnalyticsDbContextFactory contextFactory, int chunkSize)
        {
            if (chunkSize < 1) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _chunkSize = chunkSize;
        }

        public async Task<IReadOnlyDictionary<string, string>> GetUpnsByEntraObjectIdAsync(IReadOnlyCollection<string> entraObjectIds)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entraObjectIds == null || entraObjectIds.Count == 0)
            {
                return found;
            }

            var all = entraObjectIds as List<string> ?? entraObjectIds.ToList();

            using (var db = _contextFactory.Create())
            {
                for (var i = 0; i < all.Count; i += _chunkSize)
                {
                    // GetRange rather than Skip().Take(): Skip() on a List walks past every prior
                    // element, making chunking O(N^2/K).
                    var chunk = all.GetRange(i, Math.Min(_chunkSize, all.Count - i));

                    // No LOWER() on the column - the default collation is already case-insensitive,
                    // and lowering it would make the predicate non-SARGable.
                    var rows = await db.users
                        .Where(u => chunk.Contains(u.AzureAdId))
                        .Select(u => new { u.AzureAdId, u.UserPrincipalName })
                        .ToListAsync();

                    foreach (var row in rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.AzureAdId) || string.IsNullOrWhiteSpace(row.UserPrincipalName))
                        {
                            continue;
                        }
                        found[row.AzureAdId] = row.UserPrincipalName;
                    }
                }
            }

            return found;
        }

        public async Task StoreEntraObjectIdAsync(IReadOnlyDictionary<string, string> upnsByEntraObjectId)
        {
            if (upnsByEntraObjectId == null || upnsByEntraObjectId.Count == 0)
            {
                return;
            }

            var upns = upnsByEntraObjectId.Values
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (upns.Count == 0)
            {
                return;
            }

            var entraIdByUpn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in upnsByEntraObjectId)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    entraIdByUpn[pair.Value] = pair.Key;
                }
            }

            using (var db = _contextFactory.Create())
            {
                for (var i = 0; i < upns.Count; i += _chunkSize)
                {
                    var chunk = upns.GetRange(i, Math.Min(_chunkSize, upns.Count - i));

                    var users = await db.users
                        .Where(u => chunk.Contains(u.UserPrincipalName))
                        .ToListAsync();

                    foreach (var user in users)
                    {
                        string entraId;
                        if (!entraIdByUpn.TryGetValue(user.UserPrincipalName, out entraId))
                        {
                            continue;
                        }

                        // Only fill a gap. The Graph user import is the authority for this column, so
                        // an existing value is never overwritten on the strength of an audit record.
                        if (string.IsNullOrWhiteSpace(user.AzureAdId))
                        {
                            user.AzureAdId = entraId;
                        }
                    }

                    await db.SaveChangesAsync();
                }
            }
        }
    }

    /// <summary>
    /// Resolves Entra object ids to UPNs through Microsoft Graph.
    /// </summary>
    /// <remarks>
    /// One request per id, because Graph has no filter that takes a list of object ids for
    /// <c>/users</c>. That is acceptable only because the caller reaches this at all rarely: the
    /// resolver answers from SQL first and caches every outcome - including failures - for the run, so
    /// a given id costs at most one request per process. A per-event Graph call would be the
    /// anti-pattern this codebase's performance guidance calls out at the ~200,000-user design target,
    /// and <see cref="MaxLookupsPerBatch"/> is the backstop that keeps it that way if an unexpected
    /// payload ever produced a flood of unknown ids.
    /// </remarks>
    internal class GraphEntraUserLookup : IEntraUserLookup
    {
        /// <summary>
        /// Hard cap on directory reads in a single call, so a pathological batch degrades into
        /// unresolved ids (which keep today's behaviour) rather than a very long stall.
        /// </summary>
        public const int MaxLookupsPerBatch = 200;

        private readonly GraphServiceClient _graph;
        private readonly Action<string> _log;
        private readonly int _maxLookups;

        public GraphEntraUserLookup(GraphServiceClient graph, Action<string> log = null)
            : this(graph, MaxLookupsPerBatch, log) { }

        public GraphEntraUserLookup(GraphServiceClient graph, int maxLookups, Action<string> log = null)
        {
            if (maxLookups < 1) throw new ArgumentOutOfRangeException(nameof(maxLookups));
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
            _maxLookups = maxLookups;
            _log = log;
        }

        public async Task<IReadOnlyDictionary<string, string>> GetUpnsByObjectIdAsync(IReadOnlyCollection<string> entraObjectIds)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entraObjectIds == null || entraObjectIds.Count == 0)
            {
                return found;
            }

            var looked = 0;
            foreach (var id in entraObjectIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;

                if (looked >= _maxLookups)
                {
                    _log?.Invoke($"Reached the per-batch cap of {_maxLookups} Entra user lookups; " +
                                 "the remaining ids keep their raw identifier and are retried next cycle.");
                    break;
                }

                looked++;
                try
                {
                    var user = await _graph.Users[id.Trim()].GetAsync(config =>
                    {
                        config.QueryParameters.Select = new[] { "id", "userPrincipalName" };
                    });

                    if (!string.IsNullOrWhiteSpace(user?.UserPrincipalName))
                    {
                        found[id.Trim()] = user.UserPrincipalName;
                    }
                }
                catch (Exception ex)
                {
                    // A deleted user, a guest that has been purged, or an id that is not a user at
                    // all. Absent from the result, which the resolver records so it is never asked
                    // for again this run.
                    _log?.Invoke($"Entra lookup failed for an audit user id: {ex.Message}");
                }
            }

            return found;
        }
    }

    /// <summary>
    /// Builds the Graph client on first use, so a tenant whose audit records are all UPNs - or whose
    /// object ids are all answered from <c>users.azure_ad_id</c> - never authenticates to Graph for this
    /// at all.
    /// </summary>
    /// <remarks>
    /// Worth the indirection because the client needs an async credential init that cannot happen in a
    /// constructor, and because this path is expected to be cold: the resolver asks the database first
    /// and caches every outcome for the run.
    /// </remarks>
    internal class LazyGraphEntraUserLookup : IEntraUserLookup
    {
        private readonly AppConfig _appConfig;
        private readonly ILogger _logger;
        private IEntraUserLookup _inner;
        private bool _initFailed;

        public LazyGraphEntraUserLookup(AppConfig appConfig, ILogger logger)
        {
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            _logger = logger;
        }

        public async Task<IReadOnlyDictionary<string, string>> GetUpnsByObjectIdAsync(IReadOnlyCollection<string> entraObjectIds)
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entraObjectIds == null || entraObjectIds.Count == 0 || _initFailed)
            {
                return empty;
            }

            if (_inner == null)
            {
                try
                {
                    var auth = new GraphAppIndentityOAuthContext(
                        _logger, _appConfig.ClientID, _appConfig.TenantGUID.ToString(),
                        _appConfig.ClientSecret, _appConfig.KeyVaultUrl, _appConfig.UseClientCertificate);
                    await auth.InitClientCredential();
                    _inner = new GraphEntraUserLookup(new GraphServiceClient(auth.Creds), msg => _logger?.LogWarning(msg));
                }
                catch (Exception ex)
                {
                    // Remembered, so a broken credential is not retried for every batch of the cycle.
                    _initFailed = true;
                    _logger?.LogWarning(
                        $"Could not authenticate to Microsoft Graph to resolve audit user ids: {ex.Message}. " +
                        "Those events keep their raw identifier.");
                    return empty;
                }
            }

            return await _inner.GetUpnsByObjectIdAsync(entraObjectIds);
        }
    }
}

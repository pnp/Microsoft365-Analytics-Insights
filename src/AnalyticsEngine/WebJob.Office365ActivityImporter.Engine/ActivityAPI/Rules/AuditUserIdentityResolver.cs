using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>
    /// Resolves Entra ID object ids found in audit records to the user principal name the rest of the
    /// importer keys on.
    /// </summary>
    /// <remarks>
    /// A port so the resolution policy - database first, Entra only on a miss, remember both answers -
    /// can be tested without a database or a Graph client.
    /// </remarks>
    public interface IAuditUserIdentityResolver
    {
        /// <summary>
        /// Resolves the given Entra object ids to UPNs, in one batch.
        /// </summary>
        /// <returns>
        /// A map from object id to UPN. Ids that could not be resolved are simply absent - callers keep
        /// their existing behaviour for those rather than inventing a user.
        /// </returns>
        Task<IReadOnlyDictionary<string, string>> ResolveToUpnAsync(IReadOnlyCollection<string> entraObjectIds);
    }

    /// <summary>Reads the <c>azure_ad_id</c> to <c>user_name</c> mapping the user import already stores.</summary>
    public interface IUserEntraIdStore
    {
        /// <summary>UPNs for the users whose <c>azure_ad_id</c> matches one of <paramref name="entraObjectIds"/>.</summary>
        Task<IReadOnlyDictionary<string, string>> GetUpnsByEntraObjectIdAsync(IReadOnlyCollection<string> entraObjectIds);

        /// <summary>
        /// Records the Entra object id against an existing user, so the next occurrence is answered from
        /// the database instead of Entra. A user that cannot be found is skipped, not created: this is a
        /// cache-warming step, and the audit merge owns creating users.
        /// </summary>
        Task StoreEntraObjectIdAsync(IReadOnlyDictionary<string, string> upnsByEntraObjectId);
    }

    /// <summary>Looks a user up in Entra by object id.</summary>
    public interface IEntraUserLookup
    {
        /// <summary>
        /// UPNs for the given object ids. Ids Entra does not return - deleted users, or ids that are not
        /// users at all - are absent from the result rather than throwing.
        /// </summary>
        Task<IReadOnlyDictionary<string, string>> GetUpnsByObjectIdAsync(IReadOnlyCollection<string> entraObjectIds);
    }

    /// <summary>
    /// Database-first resolver: answers from <c>dbo.users.azure_ad_id</c> where it can, asks Entra only
    /// for what is left, and remembers both answers for the rest of the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering is the whole point. The Graph user import already stores every user's
    /// <c>azure_ad_id</c>, so on a tenant that runs it the database answers nearly everything and Entra
    /// is never called. Going to Graph first would add a network round-trip per unseen id to the save
    /// path for information already held locally.
    /// </para>
    /// <para>
    /// Both outcomes are cached for the lifetime of the resolver, including failures. Without the
    /// negative cache a deleted user - which by definition can never resolve - would be looked up again
    /// on every event that mentions it, which at the ~200,000-user design target is exactly the
    /// per-event Graph call this codebase's performance guidance forbids.
    /// </para>
    /// </remarks>
    public class AuditUserIdentityResolver : IAuditUserIdentityResolver
    {
        private readonly IUserEntraIdStore _store;
        private readonly IEntraUserLookup _entra;
        private readonly Action<string> _log;

        /// <summary>Object id to UPN for everything resolved so far this run.</summary>
        private readonly Dictionary<string, string> _resolved =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Object ids already proven unresolvable, so they are asked for exactly once.</summary>
        private readonly HashSet<string> _unresolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AuditUserIdentityResolver(IUserEntraIdStore store, IEntraUserLookup entra, Action<string> log = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _entra = entra ?? throw new ArgumentNullException(nameof(entra));
            _log = log;
        }

        /// <summary>Object ids resolved from Entra this run (rather than from the database).</summary>
        public int EntraLookupCount { get; private set; }

        /// <summary>Object ids that could not be resolved at all this run.</summary>
        public int UnresolvedCount => _unresolvable.Count;

        public async Task<IReadOnlyDictionary<string, string>> ResolveToUpnAsync(IReadOnlyCollection<string> entraObjectIds)
        {
            var answer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entraObjectIds == null || entraObjectIds.Count == 0)
            {
                return answer;
            }

            var outstanding = new List<string>();
            var outstandingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in entraObjectIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                var trimmed = id.Trim();

                string cached;
                if (_resolved.TryGetValue(trimmed, out cached))
                {
                    answer[trimmed] = cached;
                }
                else if (!_unresolvable.Contains(trimmed) && outstandingSet.Add(trimmed))
                {
                    outstanding.Add(trimmed);
                }
            }

            if (outstanding.Count == 0)
            {
                return answer;
            }

            // 1. The database first - the user import has usually already stored these.
            var fromStore = await _store.GetUpnsByEntraObjectIdAsync(outstanding);
            if (fromStore != null)
            {
                foreach (var pair in fromStore)
                {
                    if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                    _resolved[pair.Key] = pair.Value;
                    answer[pair.Key] = pair.Value;
                }
            }

            var stillOutstanding = outstanding.FindAll(id => !answer.ContainsKey(id));
            if (stillOutstanding.Count == 0)
            {
                return answer;
            }

            // 2. Entra for the remainder.
            IReadOnlyDictionary<string, string> fromEntra = null;
            try
            {
                fromEntra = await _entra.GetUpnsByObjectIdAsync(stillOutstanding);
            }
            catch (Exception ex)
            {
                // A directory read failing must not fail the import. The events still save with their
                // raw id, exactly as they did before this resolution existed.
                _log?.Invoke($"Could not resolve {stillOutstanding.Count} audit user id(s) against Entra: {ex.Message}. " +
                             "Those events keep their raw identifier for this cycle.");
                return answer;
            }

            var learned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fromEntra != null)
            {
                foreach (var pair in fromEntra)
                {
                    if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                    _resolved[pair.Key] = pair.Value;
                    answer[pair.Key] = pair.Value;
                    learned[pair.Key] = pair.Value;
                    EntraLookupCount++;
                }
            }

            foreach (var id in stillOutstanding)
            {
                if (!answer.ContainsKey(id))
                {
                    _unresolvable.Add(id);
                }
            }

            // 3. Write back what Entra taught us, so the next cycle answers it from SQL.
            if (learned.Count > 0)
            {
                try
                {
                    await _store.StoreEntraObjectIdAsync(learned);
                }
                catch (Exception ex)
                {
                    // Purely an optimisation for later cycles; this run already has its answer.
                    _log?.Invoke($"Resolved {learned.Count} audit user id(s) but could not record them against the user rows: {ex.Message}.");
                }
            }

            return answer;
        }
    }
}

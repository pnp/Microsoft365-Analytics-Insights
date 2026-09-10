using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Turns the Entra object ids the licensing API reports into <c>dbo.users</c> keys, so per-user credit
    /// rows carry a real foreign key instead of an opaque string.
    /// </summary>
    /// <remarks>
    /// <para>Two steps, cheapest first. Everything already in <c>dbo.users</c> is matched in one query
    /// against <c>azure_ad_id</c> - which the user import fills from Graph's <c>user.id</c>, the very same
    /// value - and only what is left over costs a directory call.</para>
    ///
    /// <para><b>Resolution never fails the import.</b> Every failure path here degrades to an unlinked row
    /// that still reports its spend against the raw object id. A cost report that loses money because a
    /// name lookup was throttled would be worse than one that shows a GUID for a day.</para>
    ///
    /// <para>Directory calls are <b>budgeted per run</b>. The user import already loads the whole directory,
    /// so anyone reaching this path is new or deleted and the normal count is a handful. A tenant that has
    /// never run the user import is the pathological case - without a cap, the first agent-cost import would
    /// issue one Graph call per user, throttling the whole job for something the user import does properly in
    /// bulk. Anything over budget is simply deferred to the next cycle.</para>
    /// </remarks>
    public class AgentCostUserResolver
    {
        /// <summary>
        /// Directory lookups allowed per run. Generous for the intended case (a few users created since the
        /// last user import) and far below the point where per-user calls would rival the bulk import.
        /// </summary>
        public const int DefaultDirectoryLookupBudget = 200;

        private readonly IAgentCostUserLinkStore _store;
        private readonly IEntraUserLookup _directory;
        private readonly ILogger _logger;
        private readonly int _directoryLookupBudget;

        public AgentCostUserResolver(IAgentCostUserLinkStore store, IEntraUserLookup directory, ILogger logger,
            int directoryLookupBudget = DefaultDirectoryLookupBudget)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _directory = directory;
            _logger = logger;
            _directoryLookupBudget = directoryLookupBudget < 0 ? 0 : directoryLookupBudget;
        }

        /// <summary>
        /// Resolves the given object ids, consulting the directory for any the database does not know.
        /// </summary>
        public async Task<AgentCostUserResolution> ResolveAsync(IEnumerable<string> objectIds)
        {
            var distinct = (objectIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinct.Count == 0)
            {
                return new AgentCostUserResolution(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0, 0, 0, 0, 0);
            }

            var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var known = await _store.GetUserIdsByObjectIdAsync(distinct);
            if (known != null)
            {
                foreach (var pair in known)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key)) resolved[pair.Key] = pair.Value;
                }
            }

            var fromDatabase = resolved.Count;
            var unknown = distinct.Where(id => !resolved.ContainsKey(id)).ToList();

            int fromDirectory = 0, notInDirectory = 0, deferred = 0, failures = 0;

            if (unknown.Count > 0 && _directory == null)
            {
                // No directory adapter wired in. Not an error: the rows stay unlinked and are retried on a
                // later cycle, once the user import has caught up with them.
                deferred = unknown.Count;
            }
            else
            {
                foreach (var objectId in unknown)
                {
                    if (fromDirectory + notInDirectory + failures >= _directoryLookupBudget)
                    {
                        deferred++;
                        continue;
                    }

                    try
                    {
                        var user = await _directory.GetUserByObjectIdAsync(objectId);

                        if (user == null || string.IsNullOrWhiteSpace(user.UserPrincipalName))
                        {
                            // Settled: the object does not exist, or exists without a usable identity.
                            // Retrying costs a call per cycle for ever and can never succeed.
                            notInDirectory++;
                            continue;
                        }

                        user.ObjectId = objectId;
                        resolved[objectId] = await _store.EnsureUserAsync(user);
                        fromDirectory++;
                    }
                    catch (Exception ex)
                    {
                        // "Could not ask", as distinct from "no such user". Left unlinked and retried.
                        failures++;
                        _logger?.LogWarning(
                            $"Agent costs - could not look up user '{objectId}' in the directory: {ex.Message}. "
                            + "Their credits are still recorded against the raw identifier and will be linked on a later cycle.");
                    }
                }
            }

            if (deferred > 0)
            {
                _logger?.LogInformation(
                    $"Agent costs - {deferred:N0} user(s) were left unlinked because this cycle's directory-lookup "
                    + $"budget of {_directoryLookupBudget:N0} was used up. Their credits are recorded and they will "
                    + "resume next cycle.");
            }

            return new AgentCostUserResolution(resolved, fromDatabase, fromDirectory, notInDirectory, deferred, failures);
        }

        /// <summary>
        /// Makes a bounded attempt at rows left unlinked by earlier runs, then applies whatever it resolved.
        /// </summary>
        /// <remarks>
        /// The import re-reads only a trailing window, so a row that fell outside it would otherwise keep its
        /// unlinked state for ever even once the user turned up in <c>dbo.users</c>. This is what makes the
        /// nullable foreign key self-healing rather than merely forgiving.
        /// </remarks>
        public async Task<AgentCostUserResolution> ResolveOutstandingAsync(int maxRows = 500)
        {
            var outstanding = await _store.GetUnresolvedObjectIdsAsync(maxRows);
            if (outstanding == null || outstanding.Count == 0)
            {
                return new AgentCostUserResolution(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0, 0, 0, 0, 0);
            }

            var resolution = await ResolveAsync(outstanding);

            if (resolution.UserIdsByObjectId.Count > 0)
            {
                var updated = await _store.ApplyUserLinksAsync(resolution.UserIdsByObjectId);
                _logger?.LogInformation(
                    $"Agent costs - linked {updated:N0} previously unattributed credit row(s) to "
                    + $"{resolution.UserIdsByObjectId.Count:N0} user(s).");
            }

            return resolution;
        }
    }
}

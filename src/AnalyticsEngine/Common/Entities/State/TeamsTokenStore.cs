using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// The delegated refresh token stored for each Team authorised for Teams deep analytics. The portal's Teams
    /// permissions page writes and removes them; the importer reads each one to read that Team's channel messages, and
    /// removes it when Microsoft Entra ID no longer accepts it.
    /// </summary>
    /// <remarks>
    /// Kept in the <see cref="StatePartitions.TeamsAuth"/> partition, one row per Team, keyed by the Team (group) id.
    /// These were Redis keys named <c>TeamToken_&lt;teamId&gt;</c> before Redis was removed; they are not carried over,
    /// so each Team is authorised again once after that upgrade.
    /// </remarks>
    public sealed class TeamsTokenStore
    {
        /// <summary>How many Teams are checked at once when the permissions page asks about a list of them.</summary>
        private const int StatusCheckConcurrency = 16;

        private readonly IKeyValueStore _store;

        public TeamsTokenStore(IKeyValueStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>The store for this deployment, or <c>null</c> when no Storage connection string is configured.</summary>
        public static TeamsTokenStore TryOpen(AppConfig config, ILogger logger = null)
        {
            var store = StateStore.TryOpen(config, StatePartitions.TeamsAuth, logger);
            return store == null ? null : new TeamsTokenStore(store);
        }

        public string Description => _store.Description;

        /// <summary>The Team's refresh token, or <c>null</c> when the Team isn't authorised.</summary>
        public Task<string> GetRefreshTokenAsync(string teamId, CancellationToken cancellationToken = default)
            => _store.GetStringAsync(ValidTeamId(teamId), cancellationToken);

        public Task SetRefreshTokenAsync(string teamId, string refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(refreshToken)) throw new ArgumentException("A refresh token is required.", nameof(refreshToken));
            return _store.SetStringAsync(ValidTeamId(teamId), refreshToken, cancellationToken: cancellationToken);
        }

        public Task RemoveRefreshTokenAsync(string teamId, CancellationToken cancellationToken = default)
            => _store.DeleteAsync(ValidTeamId(teamId), cancellationToken);

        /// <summary>
        /// Which of <paramref name="teamIds"/> have a stored token, without reading any token. The checks run a few at a
        /// time: the permissions page asks about every Team it lists, and one point read after another would make a
        /// tenant with a thousand Teams wait for a thousand round trips.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, bool>> GetAuthorisationStatusAsync(IEnumerable<string> teamIds,
            CancellationToken cancellationToken = default)
        {
            var distinct = (teamIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            using (var gate = new SemaphoreSlim(StatusCheckConcurrency))
            {
                var checks = distinct.Select(async id =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return new KeyValuePair<string, bool>(id, await _store.ExistsAsync(id, cancellationToken).ConfigureAwait(false));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                foreach (var check in await Task.WhenAll(checks).ConfigureAwait(false))
                {
                    result[check.Key] = check.Value;
                }
            }

            return result;
        }

        private static string ValidTeamId(string teamId)
        {
            if (string.IsNullOrEmpty(teamId)) throw new ArgumentException("A Team id is required.", nameof(teamId));
            return teamId;
        }
    }
}

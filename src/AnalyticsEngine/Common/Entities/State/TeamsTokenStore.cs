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
    /// permissions page writes and removes them; the importer reads each one to read that Team's channel messages,
    /// replaces it with the renewed one Entra ID issues each time it is used, and removes it when Microsoft Entra ID no
    /// longer accepts it - unless the Team was authorised again in the meantime.
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

        /// <summary>
        /// Removes the Team's authorisation unconditionally. For an administrator removing it on the Teams permissions page
        /// only: the importer must use <see cref="TryRemoveRefreshTokenAsync"/>, because its evidence that a token is dead
        /// can be hours old and must never delete an authorisation made since.
        /// </summary>
        public Task RemoveRefreshTokenAsync(string teamId, CancellationToken cancellationToken = default)
            => _store.DeleteAsync(ValidTeamId(teamId), cancellationToken);

        /// <summary>
        /// Removes the Team's refresh token after Microsoft Entra ID or Graph refused a token a crawl holds, but only if the
        /// stored token is still one of <paramref name="tokensThisCrawlOwns"/>: the token the crawl read, or a renewed one
        /// it stored itself.
        /// </summary>
        /// <returns>
        /// Whether the token was removed. False, removing nothing, when the stored token is not one the crawl owns - an
        /// administrator authorised the Team again while it ran, which must be kept - or there is no stored token any more.
        /// </returns>
        /// <remarks>
        /// The refusal is evidence about the token the crawl read, possibly hours earlier. Deleting whatever is stored now
        /// deleted any authorisation made in the meantime - say, after the password of the person whose token it was had
        /// been reset, which also revokes their refresh tokens. The delete is conditional on the row's version (an ETag in
        /// Azure Tables), so a re-authorisation that lands between the read and the delete makes it fail rather than be
        /// lost. A store without versions falls back to re-reading the token immediately before deleting it, which leaves
        /// a window of one round trip.
        /// </remarks>
        public async Task<bool> TryRemoveRefreshTokenAsync(string teamId, IReadOnlyCollection<string> tokensThisCrawlOwns,
            CancellationToken cancellationToken = default)
        {
            var key = ValidTeamId(teamId);
            if (tokensThisCrawlOwns == null || tokensThisCrawlOwns.Count == 0) return false;

            if (_store is IConditionalKeyValueStore versioned)
            {
                var stored = await versioned.GetVersionedAsync(key, cancellationToken).ConfigureAwait(false);
                if (!IsOwned(stored.Value, tokensThisCrawlOwns))
                {
                    return false;
                }
                return await versioned.TryDeleteAsync(key, stored.VersionToken, cancellationToken).ConfigureAwait(false);
            }

            var current = await _store.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
            if (!IsOwned(current, tokensThisCrawlOwns))
            {
                return false;
            }
            return await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsOwned(string storedRefreshToken, IReadOnlyCollection<string> tokensThisCrawlOwns)
            => !string.IsNullOrEmpty(storedRefreshToken) && tokensThisCrawlOwns.Contains(storedRefreshToken, StringComparer.Ordinal);

        /// <summary>
        /// Stores the refresh token Entra ID issued in place of <paramref name="redeemedRefreshToken"/>, but only if the
        /// Team's stored token is still the one that was redeemed.
        /// </summary>
        /// <returns>
        /// Whether the rotated token was stored. False when the stored token changed while it was being redeemed: an
        /// administrator removed the Team's authorisation (which must never come back), or authorised it again (which must
        /// not be overwritten with a token from the older grant).
        /// </returns>
        /// <remarks>
        /// Entra ID issues a new refresh token every time one is redeemed, and each lasts about 90 days from issue. Keeping
        /// only the token captured when the Team was authorised meant the authorisation silently lapsed about 90 days later.
        /// The write is conditional on the row's version (an ETag in Azure Tables), so a removal or re-authorisation that
        /// lands between the read and the write makes it fail rather than be undone. A store without versions falls back to
        /// re-reading the token immediately before writing it, which leaves a window of one round trip.
        /// </remarks>
        public async Task<bool> TryReplaceRefreshTokenAsync(string teamId, string redeemedRefreshToken, string rotatedRefreshToken,
            CancellationToken cancellationToken = default)
        {
            var key = ValidTeamId(teamId);
            if (string.IsNullOrEmpty(redeemedRefreshToken)) throw new ArgumentException("The redeemed refresh token is required.", nameof(redeemedRefreshToken));
            if (string.IsNullOrEmpty(rotatedRefreshToken) || string.Equals(redeemedRefreshToken, rotatedRefreshToken, StringComparison.Ordinal))
            {
                return false;
            }

            if (_store is IConditionalKeyValueStore versioned)
            {
                var stored = await versioned.GetVersionedAsync(key, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(stored.Value, redeemedRefreshToken, StringComparison.Ordinal))
                {
                    return false;
                }
                return await versioned.TrySetStringAsync(key, rotatedRefreshToken, stored.VersionToken, cancellationToken).ConfigureAwait(false);
            }

            var current = await _store.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, redeemedRefreshToken, StringComparison.Ordinal))
            {
                return false;
            }
            await _store.SetStringAsync(key, rotatedRefreshToken, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

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

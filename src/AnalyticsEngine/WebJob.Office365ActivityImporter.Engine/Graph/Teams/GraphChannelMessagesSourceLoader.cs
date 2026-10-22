using Common.Entities.Models;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Graph implementation of <see cref="IChannelMessagesSourceLoader"/>, reading channel messages as
    /// the user whose refresh token we hold (channel message content is not available to an
    /// application-only identity). Extracted from <c>TeamChannelExtensions</c>. See issue #377.
    /// </summary>
    /// <remarks>
    /// One instance reads one Team. It builds one Graph client for the Team, over an HttpClient shared by every Team; it
    /// used to build a new client - and HttpClient, and connection pool - for every channel. Failures are classified by
    /// <see cref="ChannelReadFailureRules"/>.
    ///
    /// An access token lasts 60 to 90 minutes, and one big channel - a bot or alert channel with tens of thousands of
    /// root messages, each needing its own replies request - can take longer than that to read. So the token is renewed
    /// shortly before it expires and the read carries on. Only a token refused within <see cref="FreshTokenWindow"/> of
    /// being minted in answer to an earlier refusal counts as the authorisation itself being no good; a token that merely
    /// outlived its life is replaced. Getting this wrong deleted the Team's working authorisation every time such a
    /// channel was read.
    /// </remarks>
    public class GraphChannelMessagesSourceLoader : IChannelMessagesSourceLoader
    {
        /// <summary>How many times per Team a refused access token is replaced with a freshly minted one.</summary>
        internal const int MaxAccessTokenRemintsPerTeam = 3;

        /// <summary>A token refused this soon after it was minted - in answer to an earlier refusal - didn't just expire.</summary>
        internal static readonly TimeSpan FreshTokenWindow = TimeSpan.FromMinutes(5);

        /// <summary>The access token is renewed once it has less than this left.</summary>
        internal static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromMinutes(5);

        /// <summary>The lifetime assumed when Entra ID doesn't say: the shortest it issues for Microsoft Graph.</summary>
        internal static readonly TimeSpan AssumedAccessTokenLifetime = TimeSpan.FromMinutes(60);

        private static readonly Lazy<HttpClient> SharedHttpClient = new Lazy<HttpClient>(CreateSharedHttpClient);

        private readonly RefreshOAuthToken _refreshToken;
        private readonly Func<Task<RefreshOAuthToken>> _remintAccessToken;
        private readonly ITeamChannelDeltaTokenStore _deltaTokenStore;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly Func<DateTime> _utcNow;
        private readonly DelegatedAccessToken _accessToken;
        private ChannelMessagesLoader _messagesLoader;
        private int _remintsAfterRefusal;

        /// <param name="refreshToken">
        /// User-delegated token to impersonate. May be <c>null</c>, in which case nothing is read and no
        /// delta token is returned - the same "no token, no deep analytics" behaviour as before.
        /// </param>
        public GraphChannelMessagesSourceLoader(RefreshOAuthToken refreshToken, ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger)
            : this(refreshToken, null, deltaTokenStore, logger) { }

        /// <param name="remintAccessToken">
        /// Mints a new access token from the Team's refresh token: shortly before the current one expires, and when Graph
        /// refuses it. <c>null</c>: the token can be neither renewed nor replaced, so a refusal proves nothing about it.
        /// </param>
        public GraphChannelMessagesSourceLoader(RefreshOAuthToken refreshToken, Func<Task<RefreshOAuthToken>> remintAccessToken,
            ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger)
            : this(refreshToken, remintAccessToken, deltaTokenStore, logger, null) { }

        /// <param name="httpClient">The transport, for tests; <c>null</c> uses the shared Graph pipeline.</param>
        /// <param name="utcNow">The clock that ages the access token, for tests.</param>
        internal GraphChannelMessagesSourceLoader(RefreshOAuthToken refreshToken, Func<Task<RefreshOAuthToken>> remintAccessToken,
            ITeamChannelDeltaTokenStore deltaTokenStore, ILogger logger, HttpClient httpClient, Func<DateTime> utcNow = null)
        {
            _refreshToken = refreshToken;
            _remintAccessToken = remintAccessToken;
            _deltaTokenStore = deltaTokenStore ?? throw new ArgumentNullException(nameof(deltaTokenStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (refreshToken != null)
            {
                // The Team's token was minted just before this loader was built.
                _accessToken = new DelegatedAccessToken(refreshToken, remintAccessToken, _utcNow, _logger);
            }
        }

        public async Task<TeamChannelDeltaTokenInfo> LoadMessagesAndReactions(ChannelWithReactions channel, string teamId)
        {
            // Try and get user-delegated channel stats
            if (_refreshToken == null)
            {
                return null;
            }

            var channelMessagesLoader = MessagesLoader();
            while (true)
            {
                try
                {
                    // Load msgs using user token
                    return await channelMessagesLoader.LoadTeamMessagesAndReplies(channel, teamId);
                }
                catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
                {
                    if (!ChannelReadFailureRules.IsUnauthorized(ex))
                    {
                        throw ReadFailure(ex, channel, teamId, freshTokenRejected: false);
                    }

                    // Refused. A token minted moments ago, because the one before it was refused too, means the
                    // authorisation itself is no good; anything else - a token that outlived its life - gets a new one.
                    var refused = _accessToken.Current;
                    var freshTokenRejected = refused.MintedAfterRefusal && _utcNow() - refused.MintedUtc <= FreshTokenWindow;
                    if (freshTokenRejected || !CanRemint)
                    {
                        throw ReadFailure(ex, channel, teamId, freshTokenRejected);
                    }

                    await RemintAfterRefusal(ex);
                }
            }
        }

        private bool CanRemint => _remintAccessToken != null && _remintsAfterRefusal < MaxAccessTokenRemintsPerTeam;

        private ChannelMessagesLoader MessagesLoader()
        {
            if (_messagesLoader == null)
            {
                // Managed to get user-delegated token from refresh-token. Impersonate user.
                var client = new GraphServiceClient(_httpClient ?? SharedHttpClient.Value, _accessToken);
                _messagesLoader = new ChannelMessagesLoader(client, _deltaTokenStore, _logger);
            }
            return _messagesLoader;
        }

        private async Task RemintAfterRefusal(Exception refusal)
        {
            _remintsAfterRefusal++;
            RefreshOAuthToken fresh;
            try
            {
                fresh = await _remintAccessToken();
            }
            catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
            {
                var kind = TeamTokenRefreshRules.IsRefreshTokenRejected(ex) ? ChannelReadFailureKind.TokenRejected : ChannelReadFailureKind.Transient;
                throw new ChannelMessagesReadException(kind,
                    $"Graph refused the Team's access token and a new one couldn't be minted ({TeamTokenRefreshRules.Describe(ex)}).", ex);
            }

            if (string.IsNullOrEmpty(fresh?.AccessToken))
            {
                throw new ChannelMessagesReadException(ChannelReadFailureKind.Transient,
                    "Graph refused the Team's access token and Entra ID returned no new one.", refusal);
            }

            _logger.LogInformation("Graph refused the Team's access token part-way through its channels. Minted a new one; retrying the channel.");
            _accessToken.Replace(fresh, mintedAfterRefusal: true);
        }

        private ChannelMessagesReadException ReadFailure(Exception ex, ChannelWithReactions channel, string teamId, bool freshTokenRejected)
        {
            var kind = ChannelReadFailureRules.Classify(ex, channel.MembershipType, freshTokenRejected);
            var status = ChannelReadFailureRules.StatusCode(ex);
            var answer = status.HasValue ? $"Graph answered HTTP {status}" : $"no answer from Graph ({TeamTokenRefreshRules.Describe(ex)})";
            var channelType = channel.MembershipType?.ToString().ToLowerInvariant() ?? "unknown-type";

            if (kind == ChannelReadFailureKind.SkipChannel)
            {
                _logger.LogWarning($"Skipping {channelType} channel '{channel.DisplayName}' on Team '{teamId}' this cycle: {answer}. The person who " +
                    "authorised the Team can't read it, or it was deleted. Its delta token is left alone and the Team's other channels are still read.");
            }

            return new ChannelMessagesReadException(kind, $"Couldn't read messages in {channelType} channel '{channel.DisplayName}': {answer}.", ex);
        }

        private static HttpClient CreateSharedHttpClient()
        {
            // The pipeline GraphServiceClientFactory.CreateWithTimeout builds, once per process rather than once per channel.
            var handlers = GraphClientFactory.CreateDefaultHandlers(new GraphClientOptions()).ToList();
            if (HttpMessageTracing.Current.IsEnabled)
            {
                handlers.Insert(0, new MessageTraceHandler("graph"));
            }
            var httpClient = GraphClientFactory.Create(handlers);
            httpClient.Timeout = GraphServiceClientFactory.DefaultGraphSdkTimeout;
            return httpClient;
        }

        /// <summary>
        /// Signs each request with the Team's current delegated access token. Renews it shortly before it expires, so a
        /// read longer than one token's life carries on rather than being refused part-way and starting the channel again;
        /// and takes the replacement minted after a refusal.
        /// </summary>
        private sealed class DelegatedAccessToken : IAuthenticationProvider
        {
            private readonly Func<Task<RefreshOAuthToken>> _remint;
            private readonly Func<DateTime> _utcNow;
            private readonly ILogger _logger;
            private readonly SemaphoreSlim _renewing = new SemaphoreSlim(1, 1);
            private MintedAccessToken _current;

            public DelegatedAccessToken(RefreshOAuthToken token, Func<Task<RefreshOAuthToken>> remint, Func<DateTime> utcNow, ILogger logger)
            {
                _remint = remint;
                _utcNow = utcNow;
                _logger = logger;
                _current = MintedAccessToken.From(token, utcNow(), mintedAfterRefusal: false);
            }

            public MintedAccessToken Current => Volatile.Read(ref _current);

            public void Replace(RefreshOAuthToken token, bool mintedAfterRefusal)
                => Volatile.Write(ref _current, MintedAccessToken.From(token, _utcNow(), mintedAfterRefusal));

            public async Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object> additionalAuthenticationContext = null,
                CancellationToken cancellationToken = default)
            {
                await RenewIfAboutToExpire().ConfigureAwait(false);
                request.Headers.Add("Authorization", "Bearer " + Current.AccessToken);
            }

            private async Task RenewIfAboutToExpire()
            {
                var current = Current;
                if (_remint == null || current.RenewalFailed || _utcNow() < current.ExpiresUtc - RenewBeforeExpiry)
                {
                    return;
                }

                await _renewing.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(current, Current))
                    {
                        return;
                    }

                    RefreshOAuthToken renewed = null;
                    try
                    {
                        renewed = await _remint().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!TeamsCrawlRules.IsCancellation(ex))
                    {
                        // Best effort: the current token still has a few minutes, and if Graph then refuses it, a new one is
                        // minted - and a failure classified - as for any refusal.
                        _logger.LogWarning($"Couldn't renew the Team's access token before it expires ({TeamTokenRefreshRules.Describe(ex)}). Carrying on with the current one.");
                    }

                    if (string.IsNullOrEmpty(renewed?.AccessToken))
                    {
                        current.RenewalFailed = true;
                        return;
                    }

                    Replace(renewed, mintedAfterRefusal: false);
                    _logger.LogInformation("Renewed the Team's access token before it expired; the channel read carries on.");
                }
                finally
                {
                    _renewing.Release();
                }
            }
        }

        /// <summary>An access token, with when it was minted and when it expires.</summary>
        private sealed class MintedAccessToken
        {
            private MintedAccessToken(string accessToken, DateTime mintedUtc, DateTime expiresUtc, bool mintedAfterRefusal)
            {
                AccessToken = accessToken;
                MintedUtc = mintedUtc;
                ExpiresUtc = expiresUtc;
                MintedAfterRefusal = mintedAfterRefusal;
            }

            public string AccessToken { get; }

            public DateTime MintedUtc { get; }

            public DateTime ExpiresUtc { get; }

            /// <summary>Minted because Graph refused the token before it.</summary>
            public bool MintedAfterRefusal { get; }

            /// <summary>Renewing this token ahead of expiry failed, so it isn't tried again on every request.</summary>
            public bool RenewalFailed { get; set; }

            public static MintedAccessToken From(RefreshOAuthToken token, DateTime nowUtc, bool mintedAfterRefusal)
            {
                var lifetime = token.ExpiresIn.HasValue && token.ExpiresIn.Value > 0
                    ? TimeSpan.FromSeconds(token.ExpiresIn.Value)
                    : AssumedAccessTokenLifetime;
                return new MintedAccessToken(token.AccessToken, nowUtc, nowUtc + lifetime, mintedAfterRefusal);
            }
        }
    }
}

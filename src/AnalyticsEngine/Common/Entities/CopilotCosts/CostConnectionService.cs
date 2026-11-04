using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotCosts
{
    public sealed class CostToken
    {
        public string AccessToken { get; set; }
        public string IdToken { get; set; }
        public string AccountId { get; set; }
        public string TenantId { get; set; }
        public byte[] Cache { get; set; }
    }

    public interface ICostIdentity
    {
        Task<CostToken> RedeemAsync(string code, string verifier, string nonce, CancellationToken cancellationToken);
        Task<CostToken> RefreshAsync(string accountId, byte[] cache, CancellationToken cancellationToken);
    }

    /// <summary>
    /// How the delegated administrator's MSAL token cache is protected in the <c>CopilotCostConnections</c> table:
    /// encrypt-then-MAC with keys from the runtime credential (<see cref="AgentCostTokenProtection"/>), as the
    /// Copilot Studio billing connection protects its own, under a purpose of its own.
    /// </summary>
    /// <remarks>
    /// A protected cache is stored as <see cref="Prefix"/> followed by the envelope. Builds before this one stored the
    /// cache as plain base64, which never contains a colon: such a value is still read, once, and the first write after
    /// that stores it protected. A cache that can't be unprotected - the runtime secret or certificate has changed, or
    /// the value was altered - needs the administrator to reconnect, never a fallback to the unprotected bytes.
    /// </remarks>
    public static class CostTokenCacheProtection
    {
        public const string Purpose = "AnalyticsState.CopilotCostDelegatedAuth.v1";
        public const string Prefix = "protected:";

        public static bool IsProtected(string value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>The protection for this deployment's runtime credential, built when it is first needed.</summary>
        public static Func<CancellationToken, Task<AgentCostTokenProtection>> ForConfig(AppConfig config) => async ct =>
        {
            System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = null;
            if (config.UseClientCertificate)
            {
                try
                {
                    certificate = await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName, config.KeyVaultUrl, NullLogger.Instance);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    throw new CostConnectionException("identityUnavailable");
                }
            }
            try { return new AgentCostTokenProtection(config.ClientSecret, certificate, Purpose); }
            catch (ArgumentException) { throw new CostConnectionException("configurationRequired"); }
        };
    }

    public sealed class CostConnectionService
    {
        internal const string Key = "connection";
        private readonly ICostDocumentStore _store;
        private readonly ICostIdentity _identity;
        private readonly string _tenant;
        private readonly string _client;
        private readonly string _callback;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ICostPublicationFence _publicationFence;
        private readonly Func<CancellationToken, Task<AgentCostTokenProtection>> _protectionFactory;
        private readonly object _protectionGate = new object();
        private Task<AgentCostTokenProtection> _protection;
        private DateTimeOffset _protectionBuiltAt;

        public CostConnectionService(ICostDocumentStore store, ICostIdentity identity, string tenant, string client,
            string callback, Func<CancellationToken, Task<AgentCostTokenProtection>> protection,
            Func<DateTimeOffset> clock = null, ICostPublicationFence publicationFence = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _protectionFactory = protection ?? throw new ArgumentNullException(nameof(protection));
            _tenant = Guid.Parse(tenant).ToString();
            _client = Guid.Parse(client).ToString();
            var uri = new Uri(callback);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/copilot-cost/callback" || uri.Query != "" || uri.Fragment != "")
                throw new ArgumentException("Invalid cost callback.", nameof(callback));
            _callback = uri.AbsoluteUri;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _publicationFence = publicationFence;
        }

        public async Task<CostConnectionState> ReadAsync(CancellationToken ct)
        {
            var doc = await _store.ReadAsync(Key, ct);
            return doc == null ? new CostConnectionState() : CostConnectionEncoding.Deserialize<CostConnectionState>(doc.Content);
        }

        public async Task<string> StartAsync(string operatorId, string browserSecret, CancellationToken ct)
        {
            var lease = await AcquireAsync(ct);
            try
            {
                var state = CostConnectionEncoding.Random();
                var verifier = CostConnectionEncoding.Random();
                lease.State.Pending = new CostAuthorization
                {
                    StateHash = CostConnectionEncoding.Hash(state),
                    BrowserHash = CostConnectionEncoding.Hash(browserSecret),
                    OperatorId = operatorId,
                    Verifier = verifier,
                    Nonce = CostConnectionEncoding.Random(),
                    ExpiresAt = _clock().AddMinutes(10)
                };
                await lease.SaveAsync(ct);
                return $"https://login.microsoftonline.com/{_tenant}/oauth2/v2.0/authorize?client_id={_client}"
                    + "&response_type=code&response_mode=form_post&redirect_uri=" + Uri.EscapeDataString(_callback)
                    + "&scope=" + Uri.EscapeDataString(CostScopes.Authorization)
                    + "&state=" + Uri.EscapeDataString(state)
                    + "&nonce=" + Uri.EscapeDataString(lease.State.Pending.Nonce)
                    + "&code_challenge_method=S256&code_challenge="
                    + Uri.EscapeDataString(CostConnectionEncoding.Hash(verifier).TrimEnd('=').Replace('+', '-').Replace('/', '_'))
                    + "&prompt=select_account";
            }
            finally { await lease.ReleaseAsync(); }
        }

        public async Task StageCallbackAsync(string code, string state, bool failed, string browserSecret, CancellationToken ct)
        {
            var lease = await AcquireAsync(ct);
            try
            {
                var pending = lease.State.Pending;
                if (pending == null || pending.ExpiresAt <= _clock() || pending.StateHash == null
                    || pending.StateHash != CostConnectionEncoding.Hash(state)
                    || pending.BrowserHash != CostConnectionEncoding.Hash(browserSecret)
                    || string.IsNullOrEmpty(browserSecret) || (!failed && string.IsNullOrEmpty(code)) || code?.Length > 8192)
                    throw new CostConnectionException("invalidCallback");
                pending.StateHash = null;
                pending.ResponseCode = failed ? null : code;
                pending.ResponseFailed = failed;
                await lease.SaveAsync(ct);
            }
            finally { await lease.ReleaseAsync(); }
        }

        public async Task CompleteAsync(string operatorId, string browserSecret, CancellationToken ct)
        {
            var lease = await AcquireAsync(ct);
            try
            {
                var pending = lease.State.Pending;
                if (pending == null || pending.ExpiresAt <= _clock()
                    || pending.StateHash != null
                    || pending.BrowserHash != CostConnectionEncoding.Hash(browserSecret)
                    || pending.OperatorId != operatorId || (!pending.ResponseFailed && string.IsNullOrEmpty(pending.ResponseCode))
                    || string.IsNullOrEmpty(browserSecret))
                    throw new CostConnectionException("invalidCallback");
                lease.State.Pending = null;
                await lease.SaveAsync(ct); // Consume once before the token endpoint is called.
                if (pending.ResponseFailed) throw new CostConnectionException("reconnectRequired");
                var token = await _identity.RedeemAsync(pending.ResponseCode, pending.Verifier, pending.Nonce, ct);
                if (token.TenantId != _tenant || string.IsNullOrEmpty(token.AccountId)
                    || (lease.State.AccountId != null && token.AccountId != lease.State.AccountId))
                    throw new CostConnectionException("accountMismatch");
                lease.State.AccountId = token.AccountId;
                lease.State.Cache = await ProtectCacheAsync(token.Cache, ct);
                lease.State.Status = "connected";
                lease.State.ConnectedAt = _clock();
                lease.State.RefreshedAt = _clock();
                lease.State.Generation = Guid.NewGuid().ToString("N");
                lease.State.Probe = null;
                if (_publicationFence == null) await lease.SaveAsync(ct);
                else await _publicationFence.ExecuteExclusiveAsync(lease.SaveAsync, ct);
            }
            finally { await lease.ReleaseAsync(); }
        }

        public async Task DisconnectAsync(CancellationToken ct)
        {
            if (_publicationFence == null) await DisconnectCoreAsync(ct);
            else await _publicationFence.InvalidateAndExecuteExclusiveAsync(
                async c => (await ReadAsync(c)).Generation, DisconnectCoreAsync, ct);
        }

        private async Task DisconnectCoreAsync(CancellationToken ct)
        {
            var doc = await _store.ReadAsync(Key, ct);
            // The SQL publication lock must be held before changing this Table generation.
            await _store.WriteAsync(Key, CostConnectionEncoding.Serialize(new CostConnectionState()), doc?.Version, ct);
        }

        public async Task RunWithTokenAsync(Func<CostConnectionState, string, CancellationToken, Task> action, CancellationToken ct)
        {
            var lease = await AcquireAsync(ct);
            try
            {
                if (lease.State.AccountId == null || lease.State.Cache == null)
                    throw new CostConnectionException("disconnected");
                var health = lease.State.CollectionHealth ?? (lease.State.CollectionHealth = new CostApiHealth());
                health.LastAttempt = _clock();
                var tokenAcquired = false;
                try
                {
                    var cache = await ReadCacheAsync(lease.State.Cache, ct);
                    var token = await _identity.RefreshAsync(lease.State.AccountId, cache, ct);
                    tokenAcquired = true;
                    if (token.AccountId != lease.State.AccountId || token.TenantId != _tenant)
                        throw new CostConnectionException("accountMismatch");
                    lease.State.Cache = await ProtectCacheAsync(token.Cache, ct);
                    lease.State.RefreshedAt = _clock();
                    lease.State.Status = "connected";
                    await lease.SaveAsync(ct);
                    await action(lease.State, token.AccessToken, ct);
                    await lease.SaveAsync(ct);
                }

                catch (CostConnectionException ex)
                {
                    if (ex.Status != "busy")
                        health.RecordFailure(ex.Status, _clock());
                    if (ex.Status == "reconnectRequired" || ex.Status == "permissionDenied")
                    {
                        lease.State.Status = ex.Status;
                    }
                    throw;
                }
                catch (OperationCanceledException) when (!tokenAcquired)
                {
                    health.RecordFailure("identityUnavailable", _clock());
                    throw;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    health.RecordFailure("sourceFailure", _clock());
                    throw;
                }
            }
            finally { await lease.ReleaseAsync(); }
        }

        public async Task AssertOwnershipAsync(CostConnectionState expected, CancellationToken ct)
        {
            var current = await ReadAsync(ct);
            if (current.Generation != expected.Generation || current.Owner != expected.Owner
                || current.Fence != expected.Fence || current.OwnedUntil <= _clock())
                throw new CostConnectionException("busy");
        }

        public async Task ResetProbeAsync(CancellationToken ct)
        {
            var lease = await AcquireAsync(ct);
            try
            {
                if (lease.State.AccountId == null) throw new CostConnectionException("disconnected");
                lease.State.Probe = null;
                await lease.SaveAsync(ct);
            }
            finally { await lease.ReleaseAsync(); }
        }

        private async Task<Lease> AcquireAsync(CancellationToken ct)
        {
            var doc = await _store.ReadAsync(Key, ct);
            var state = doc == null ? new CostConnectionState() : CostConnectionEncoding.Deserialize<CostConnectionState>(doc.Content);
            if (state.Owner != null && state.OwnedUntil > _clock()) throw new CostConnectionException("busy");
            state.Owner = Guid.NewGuid().ToString("N");
            state.OwnedUntil = _clock().AddMinutes(2);
            state.Fence++;
            await ProtectLegacyCacheAsync(state, ct);
            var version = await _store.WriteAsync(Key, CostConnectionEncoding.Serialize(state), doc?.Version, ct);
            return new Lease(_store, state, version, _clock);
        }

        /// <summary>
        /// A cache stored unprotected by an earlier build is protected by the first write that follows - every operation
        /// starts by writing its lease - so it is read in the clear at most once more.
        /// </summary>
        private async Task ProtectLegacyCacheAsync(CostConnectionState state, CancellationToken ct)
        {
            if (state.Cache == null || CostTokenCacheProtection.IsProtected(state.Cache)) return;
            byte[] legacy;
            try { legacy = Convert.FromBase64String(state.Cache); }
            catch (FormatException) { return; } // Left for RunWithTokenAsync to report as storageCorrupt.
            state.Cache = await ProtectCacheAsync(legacy, ct);
        }

        private async Task<string> ProtectCacheAsync(byte[] cache, CancellationToken ct)
        {
            if (cache == null) throw new CostConnectionException("reconnectRequired");
            return CostTokenCacheProtection.Prefix + (await ProtectionAsync(ct)).Protect(cache);
        }

        private async Task<byte[]> ReadCacheAsync(string stored, CancellationToken ct)
        {
            if (!CostTokenCacheProtection.IsProtected(stored))
            {
                try { return Convert.FromBase64String(stored); }
                catch (FormatException) { throw new CostConnectionException("storageCorrupt"); }
            }
            var protection = await ProtectionAsync(ct);
            try { return protection.Unprotect(stored.Substring(CostTokenCacheProtection.Prefix.Length)); }
            catch (Exception ex) when (ex is CryptographicException || ex is Newtonsoft.Json.JsonException
                || ex is FormatException || ex is ArgumentException)
            {
                // The runtime secret or certificate changed, or the value was altered. Never fall back to anything else.
                throw new CostConnectionException("reconnectRequired");
            }
        }

        /// <summary>
        /// Built once and reused for <see cref="ProtectionLifetime"/>, then built again, as it is after a failure (Key Vault
        /// unavailable, say). The web app's service lives as long as the process, while the importer builds one per slice:
        /// without a limit, a certificate rotated in Key Vault would leave the web app protecting the cache with a key the
        /// importer can no longer unwrap until the web app restarted.
        /// </summary>
        private Task<AgentCostTokenProtection> ProtectionAsync(CancellationToken ct)
        {
            lock (_protectionGate)
            {
                if (_protection == null || _protection.IsFaulted || _protection.IsCanceled
                    || _clock() - _protectionBuiltAt >= ProtectionLifetime)
                {
                    _protection = _protectionFactory(ct);
                    _protectionBuiltAt = _clock();
                }
                return _protection;
            }
        }

        internal static readonly TimeSpan ProtectionLifetime = TimeSpan.FromMinutes(10);

        private sealed class Lease
        {
            private readonly ICostDocumentStore _store;
            private readonly Func<DateTimeOffset> _clock;
            private string _version;
            public CostConnectionState State { get; }
            public Lease(ICostDocumentStore store, CostConnectionState state, string version, Func<DateTimeOffset> clock)
            {
                _store = store; State = state; _version = version; _clock = clock;
            }
            public async Task SaveAsync(CancellationToken ct)
            {
                if (State.OwnedUntil <= _clock()) throw new CostConnectionException("busy");
                _version = await _store.WriteAsync(Key, CostConnectionEncoding.Serialize(State), _version, ct);
            }
            public async Task ReleaseAsync()
            {
                if (State.OwnedUntil <= _clock()) return;
                State.Owner = null;
                State.OwnedUntil = null;
                try
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                        await _store.WriteAsync(Key, CostConnectionEncoding.Serialize(State), _version, timeout.Token);
                }
                catch (OperationCanceledException) { throw new CostConnectionException("storageUnavailable"); }
                catch (CostConnectionException ex) when (ex.Status == "busy")
                {
                    // Disconnect or a newer owner already fenced this operation. Never overwrite it.
                }
            }
        }
    }

    public static class CostScopes
    {
        public static readonly string[] Graph =
        {
            "https://graph.microsoft.com/CopilotCostManagement-UserData.Read.All",
            "https://graph.microsoft.com/CopilotCostManagement-Policy.Read.All",
            "https://graph.microsoft.com/CopilotCostManagement-Assignment.Read.All",
            "https://graph.microsoft.com/CopilotCostManagement.Read.All"
        };
        public static string Authorization => "openid profile offline_access " + string.Join(" ", Graph);
    }
}

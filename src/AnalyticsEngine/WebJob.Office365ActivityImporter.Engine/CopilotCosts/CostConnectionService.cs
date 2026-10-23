using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.CopilotCosts
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

    public sealed class CostConnectionService
    {
        private const string Key = "connection";
        private readonly ICostDocumentStore _store;
        private readonly ICostIdentity _identity;
        private readonly string _tenant;
        private readonly string _client;
        private readonly string _callback;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ICostPublicationFence _publicationFence;

        public CostConnectionService(ICostDocumentStore store, ICostIdentity identity, string tenant, string client,
            string callback, Func<DateTimeOffset> clock = null, ICostPublicationFence publicationFence = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
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
                lease.State.Cache = Convert.ToBase64String(token.Cache);
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
                try
                {
                    byte[] cache;
                    try { cache = Convert.FromBase64String(lease.State.Cache); }
                    catch (FormatException) { throw new CostConnectionException("storageCorrupt"); }
                    var token = await _identity.RefreshAsync(lease.State.AccountId, cache, ct);
                    if (token.AccountId != lease.State.AccountId || token.TenantId != _tenant)
                        throw new CostConnectionException("accountMismatch");
                    lease.State.Cache = Convert.ToBase64String(token.Cache);
                    lease.State.RefreshedAt = _clock();
                    lease.State.Status = "connected";
                    await lease.SaveAsync(ct);
                    await action(lease.State, token.AccessToken, ct);
                    await lease.SaveAsync(ct);
                }

                catch (CostConnectionException ex)
                {
                    if (ex.Status == "reconnectRequired" || ex.Status == "permissionDenied")
                    {
                        lease.State.Status = ex.Status;
                        await lease.SaveAsync(ct);
                    }
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
            var version = await _store.WriteAsync(Key, CostConnectionEncoding.Serialize(state), doc?.Version, ct);
            return new Lease(_store, state, version, _clock);
        }

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

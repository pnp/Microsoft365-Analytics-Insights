using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Common.Entities.Config;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Common.Entities.State;

namespace Common.Entities.CopilotCosts
{
    public static class CostCollectorRuntime
    {
        public static SqlCostCollectorStore CreateHistoryStore(AppConfig config) =>
            new SqlCostCollectorStore(() => AzureSqlTokenAuth.CreateConnection(config.ConnectionStrings.DatabaseConnectionString));

        public static ICostDocumentStore CreateDocumentStore(AppConfig config)
        {
            try
            {
                var options = new TableClientOptions();
                options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
                options.Retry.MaxRetries = 1;
                return new TableCostDocumentStore(
                    StorageTableClientFactory.CreateAndEnsureTable(config.ConnectionStrings.StorageConnectionString,
                        "CopilotCostConnections", config, NullLogger.Instance, "Copilot cost connection", options),
                    config.TenantGUID.ToString(), config.ClientID);
            }
            catch (RequestFailedException) { throw new CostConnectionException("storageUnavailable"); }
            catch (AuthenticationFailedException) { throw new CostConnectionException("storageUnavailable"); }
            catch (ArgumentException) { throw new CostConnectionException("configurationRequired"); }
            catch (InvalidOperationException) { throw new CostConnectionException("configurationRequired"); }
        }

        // This gate precedes ALL cache, token, HTTP and SQL construction. The delegate seam makes the
        // disabled/missing-storage contract independently testable without constructing runtime clients.
        public static async Task<string> RunIfEnabledAsync(bool enabled, string storage,
            Func<CancellationToken, Task<string>> run, CancellationToken ct)
        {
            if (!enabled) return "disabled";
            if (string.IsNullOrWhiteSpace(storage)) return "storageRequired";
            try { return await run(ct); }
            catch (CostConnectionException ex) { return CostCollector.Outcomes.Contains(ex.Status) ? ex.Status : "configurationRequired"; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "sliceTimedOut"; }
            catch (Exception) { return "sourceFailure"; } // Never log SDK/SQL exception text or identifiers.
        }

        public static Task<string> RunAsync(AppConfig config, CancellationToken ct) =>
            RunIfEnabledAsync(config.ImportJobSettings.CopilotCostManagement && config.CopilotCostConnectionEnabled,
                config.ConnectionStrings?.StorageConnectionString, async cancellation =>
                {
                    if (!Uri.TryCreate(config.WebAppURL, UriKind.Absolute, out var web) || web.Scheme != "https"
                        || web.UserInfo != "" || web.Query != "" || web.Fragment != "" || web.AbsolutePath != "/")
                        return "configurationRequired";
                    var callback = new Uri(web, "/copilot-cost/callback").AbsoluteUri;
                    var history = CreateHistoryStore(config);
                    var identity = new CollectorIdentity(config, callback);
                    var connection = new CostConnectionService(CreateDocumentStore(config), identity,
                        config.TenantGUID.ToString(), config.ClientID, callback, publicationFence: history);
                    using (var slice = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                    {
                        slice.CancelAfter(TimeSpan.FromSeconds(40));
                        return await new CostCollector(history, connection, http, identity.DirectoryTokenAsync).RunSliceAsync(slice.Token);
                    }
                }, ct);

        // Redemption and OIDC validation remain in Web. Only silent, account-bound acquisition is
        // shared with the WebJob; no visitor cookie, interactive flow or delegated directory token.
        private sealed class CollectorIdentity : ICostIdentity
        {
            private readonly AppConfig _config;
            private readonly string _callback;
            public CollectorIdentity(AppConfig config, string callback) { _config = config; _callback = callback; }
            public Task<CostToken> RedeemAsync(string code, string verifier, string nonce, CancellationToken ct) =>
                throw new CostConnectionException("reconnectRequired");

            private async Task<IConfidentialClientApplication> AppAsync()
            {
                try
                {
                    return await AuthHelper.GetNewClientApp(_config.TenantGUID.ToString(), _config.ClientID,
                        _config.ClientSecret, _config.KeyVaultUrl, _config.UseClientCertificate, NullLogger.Instance, _callback);
                }
                catch (Exception) { throw new CostConnectionException("identityUnavailable"); }
            }

            public async Task<string> DirectoryTokenAsync(CancellationToken ct)
            {
                try
                {
                    var app = await AppAsync();
                    return (await app.AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" }).ExecuteAsync(ct)).AccessToken;
                }
                catch (MsalException) { throw new CostConnectionException("identityUnavailable"); }
            }

            public async Task<CostToken> RefreshAsync(string accountId, byte[] cache, CancellationToken ct)
            {
                try
                {
                    var app = await AppAsync();
                    var serialized = cache;
                    app.UserTokenCache.SetBeforeAccess(args =>
                        args.TokenCache.DeserializeMsalV3(serialized, shouldClearExistingCache: true));
                    app.UserTokenCache.SetAfterAccess(args => serialized = args.TokenCache.SerializeMsalV3());
                    var account = await app.GetAccountAsync(accountId);
                    if (account == null) throw new CostConnectionException("reconnectRequired");
                    var token = await app.AcquireTokenSilent(CostScopes.Graph, account).ExecuteAsync(ct);
                    if (CostScopes.Graph.Any(scope => !token.Scopes.Any(granted =>
                        string.Equals(granted.Replace("https://graph.microsoft.com/", ""), scope.Replace("https://graph.microsoft.com/", ""),
                            StringComparison.OrdinalIgnoreCase))))
                        throw new CostConnectionException("permissionDenied");
                    return new CostToken { AccessToken = token.AccessToken, AccountId = token.Account.HomeAccountId.Identifier,
                        TenantId = token.TenantId, Cache = serialized };
                }
                catch (MsalUiRequiredException) { throw new CostConnectionException("reconnectRequired"); }
                catch (MsalException) { throw new CostConnectionException("identityUnavailable"); }
            }
        }
    }
}

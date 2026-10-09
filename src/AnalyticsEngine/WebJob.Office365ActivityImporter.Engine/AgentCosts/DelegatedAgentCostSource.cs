using Azure.Core;
using Common.Entities.State;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>Bearer injection without the optional raw HTTP message tracer: billing payloads stay private.</summary>
    public sealed class AgentCostDelegatedHttpClient : AutoThrottleHttpClient
    {
        public const string ReconnectDiagnostic = "agentCosts.import.reconnectNeeded";
        public const string TokenUnavailableDiagnostic = "agentCosts.import.tokenUnavailable";

        /// <summary>
        /// Stored in the import log instead of an error when there is no delegated connection. Kept equal to
        /// <c>AgentCostImportNames.ConnectionRequired</c>, which the report store reads.
        /// </summary>
        public const string ConnectionRequiredDiagnostic = "agentCosts.import.connectionRequired";

        public AgentCostDelegatedHttpClient(ILogger logger, AgentCostDelegatedTokenProvider provider, string version)
            : base(false, logger, new BillingAuthHandler(provider, version))
        {
        }

        private sealed class BillingAuthHandler : DelegatingHandler
        {
            private readonly AgentCostDelegatedTokenProvider _provider;
            private readonly string _version;
            private readonly SemaphoreSlim _tokenGate = new SemaphoreSlim(1, 1);
            private AccessToken _token;

            public BillingAuthHandler(AgentCostDelegatedTokenProvider provider, string version)
            {
                _provider = provider;
                _version = version;
                InnerHandler = new HttpClientHandler();
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // The importer reads per-user pages concurrently. One caller renews the token while the rest
                // wait for it: concurrent redemptions of a rotating refresh token could invalidate each other.
                string token;
                await _tokenGate.WaitAsync(cancellationToken);
                try
                {
                    if (_token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
                    {
                        try { _token = await _provider.AcquireAsync(_version); }
                        catch (AgentCostConnectionException ex)
                        {
                            if (ex.Code == "tokenUnavailable")
                                throw new InvalidOperationException(TokenUnavailableDiagnostic);
                            throw new AgentCostAuthorisationException(ReconnectDiagnostic);
                        }
                    }
                    token = _token.Token;
                }
                finally { _tokenGate.Release(); }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await base.SendAsync(request, cancellationToken);
            }
        }
    }

    /// <summary>Consumption uses the selected connection; capacity keeps its independently working app identity.</summary>
    public sealed class DelegatedAgentCostSource : ICopilotStudioCreditSource
    {
        private readonly ICopilotStudioCreditSource _consumption;
        private readonly ICopilotStudioCreditSource _capacity;
        private readonly AgentCostConnectionStore _store;
        private readonly string _version;

        public DelegatedAgentCostSource(ICopilotStudioCreditSource consumption, ICopilotStudioCreditSource capacity,
            AgentCostConnectionStore store, string version)
        {
            _consumption = consumption;
            _capacity = capacity;
            _store = store;
            _version = version;
        }

        public bool CanReadConsumption => true;

        public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync() => _capacity.GetCapacityAsync();
        public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync() => _consumption.GetEnvironmentNamesAsync();

        // Every consumption route is delegated, and a refusal on any of them means the connection no longer
        // grants what the import needs, so each goes through the reconnect handling.
        public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken) =>
            WithReconnectAsync(() => _consumption.GetUserConsumptionPageAsync(fromDate, toDate, continuationToken));

        public Task<CopilotStudioCreditPage> GetUserResourceConsumptionPageAsync(string userId, DateTime day, string continuationToken) =>
            WithReconnectAsync(() => _consumption.GetUserResourceConsumptionPageAsync(userId, day, continuationToken));

        private async Task<T> WithReconnectAsync<T>(Func<Task<T>> read)
        {
            try { return await read(); }
            catch (AgentCostAuthorisationException)
            {
                await _store.RequireReconnectAsync(_version);
                throw new AgentCostAuthorisationException(AgentCostDelegatedHttpClient.ReconnectDiagnostic);
            }
        }
    }
}

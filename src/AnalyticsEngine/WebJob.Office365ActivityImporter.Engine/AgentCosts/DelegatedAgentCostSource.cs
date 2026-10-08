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
        public const string UserAccessDeniedDiagnostic = "agentCosts.import.userAccessDenied";

        public AgentCostDelegatedHttpClient(ILogger logger, AgentCostDelegatedTokenProvider provider, string version)
            : base(false, logger, new BillingAuthHandler(provider, version))
        {
        }

        private sealed class BillingAuthHandler : DelegatingHandler
        {
            private readonly AgentCostDelegatedTokenProvider _provider;
            private readonly string _version;
            private AccessToken _token;

            public BillingAuthHandler(AgentCostDelegatedTokenProvider provider, string version)
            {
                _provider = provider;
                _version = version;
                InnerHandler = new HttpClientHandler();
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);
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

        public Task<CopilotStudioCapacitySnapshot> GetCapacityAsync() => _capacity.GetCapacityAsync();
        public Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync() => _consumption.GetEnvironmentNamesAsync();

        public Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken) =>
            WithReconnectAsync(() => _consumption.GetConsumptionPageAsync(fromDate, toDate, continuationToken));

        public async Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken)
        {
            try { return await _consumption.GetUserConsumptionPageAsync(fromDate, toDate, continuationToken); }
            catch (AgentCostAuthorisationException ex)
            {
                if (ex.Message == AgentCostDelegatedHttpClient.ReconnectDiagnostic) throw;
                // A route-specific refusal must not disable independently successful aggregate reads.
                throw new AgentCostAuthorisationException(AgentCostDelegatedHttpClient.UserAccessDeniedDiagnostic);
            }
        }

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

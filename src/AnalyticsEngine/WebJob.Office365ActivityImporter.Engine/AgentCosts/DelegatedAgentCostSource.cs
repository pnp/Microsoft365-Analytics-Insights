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
        /// The delegated token was accepted but Microsoft refused the licensing route (HTTP 403). Reconnecting alone
        /// does not fix it, so it is reported separately from <see cref="ReconnectDiagnostic"/> (#914).
        /// </summary>
        public const string RouteRefusedDiagnostic = "agentCosts.import.routeRefused";

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
        private readonly SemaphoreSlim _routeStateGate = new SemaphoreSlim(1, 1);
        private bool _routeAnswered;
        private bool _routeRefused;

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

        // Every consumption route is delegated. A token Microsoft no longer accepts (HTTP 401, or a refresh that
        // fails) means the connection must be renewed. A refused route (HTTP 403) does not: the sign-in is valid,
        // so it is recorded as its own state rather than sending the administrator round a reconnect loop (#914).
        public Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken) =>
            WithReconnectAsync(() => _consumption.GetUserConsumptionPageAsync(fromDate, toDate, continuationToken));

        public Task<CopilotStudioCreditPage> GetUserResourceConsumptionPageAsync(string userId, DateTime day, string continuationToken) =>
            WithReconnectAsync(() => _consumption.GetUserResourceConsumptionPageAsync(userId, day, continuationToken));

        private async Task<T> WithReconnectAsync<T>(Func<Task<T>> read)
        {
            T result;
            try { result = await read(); }
            catch (AgentCostRouteRefusedException ex)
            {
                await RecordRouteStateAsync(refused: true);
                throw new AgentCostRouteRefusedException(AgentCostDelegatedHttpClient.RouteRefusedDiagnostic, ex);
            }
            catch (AgentCostAuthorisationException)
            {
                await _store.RequireReconnectAsync(_version);
                throw new AgentCostAuthorisationException(AgentCostDelegatedHttpClient.ReconnectDiagnostic);
            }
            await RecordRouteStateAsync(refused: false);
            return result;
        }

        /// <summary>
        /// A refusal is recorded every time; a successful read clears an earlier run's refusal once, and only while this
        /// run has not been refused itself. Per-user reads run concurrently, so both go through one gate: a success that
        /// finishes after a concurrent refusal must not erase it.
        /// </summary>
        private async Task RecordRouteStateAsync(bool refused)
        {
            if (!refused && (_routeAnswered || _routeRefused)) return;
            await _routeStateGate.WaitAsync();
            try
            {
                if (refused)
                {
                    _routeRefused = true;
                    await _store.RefuseRouteAsync(_version);
                }
                else if (!_routeAnswered && !_routeRefused)
                {
                    _routeAnswered = true;
                    await _store.ClearRouteRefusedAsync(_version);
                }
            }
            catch (Exception)
            {
                // Fail open: the state only guides the administrator, so a Storage outage must neither fail a read
                // that worked nor hide the refusal behind a Storage error. The next run records it again.
            }
            finally { _routeStateGate.Release(); }
        }
    }
}

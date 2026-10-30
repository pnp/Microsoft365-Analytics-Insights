using Common.Entities.Config;
using Common.Entities.State;
using DataUtils.Health;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotCosts
{
    public sealed class CostApiConnectionHealth
    {
        [JsonProperty("source")]
        public string Source { get; set; }
        [JsonProperty("status")]
        public string Status { get; set; }
        [JsonProperty("lastAttempt")]
        public DateTimeOffset? LastAttempt { get; set; }
        [JsonProperty("lastSuccessfulRead")]
        public DateTimeOffset? LastSuccessfulRead { get; set; }
        [JsonProperty("failureSince")]
        public DateTimeOffset? FailureSince { get; set; }
        [JsonProperty("failureCode")]
        public string FailureCode { get; set; }

        public RuntimeHealthResult ToRuntimeResult(DateTimeOffset now)
        {
            switch (Status)
            {
                case "healthy":
                    return new RuntimeHealthResult(HealthStatus.Healthy,
                        "Cost API access has succeeded. This does not prove complete or current cost data.", reasonKey: "costApi.healthy");
                case "disabled":
                    return new RuntimeHealthResult(HealthStatus.Healthy,
                        "Cost collection is disabled.", reasonKey: "costApi.disabled");
                case "disconnected":
                    return new RuntimeHealthResult(HealthStatus.Degraded,
                        "No delegated cost connection is configured.", reasonKey: "costApi.disconnected");
                case "pending":
                    return new RuntimeHealthResult(HealthStatus.Degraded,
                        "An administrator connection is saved, but no successful cost API read has been recorded.", reasonKey: "costApi.pending");
                case "reconnectRequired":
                    return new RuntimeHealthResult(HealthStatus.Unhealthy,
                        "The delegated cost connection needs renewal. Reconnect in Administration.", reasonKey: "costApi.reconnectRequired");
                case "permissionDenied":
                    return new RuntimeHealthResult(HealthStatus.Unhealthy,
                        "Cost API access was refused. Check the delegated account's permissions in Administration.", reasonKey: "costApi.permissionDenied");
                case "failing":
                    return new RuntimeHealthResult(FailureSince <= now.AddMinutes(-15) ? HealthStatus.Unhealthy : HealthStatus.Degraded,
                        "Cost API collection is failing; recent cost data may be incomplete. Check Administration.", reasonKey: "costApi.failing");
                default:
                    return new RuntimeHealthResult(HealthStatus.Unhealthy,
                        "Cost connection health could not be read. Check Azure Table storage access.", reasonKey: "costApi.unavailable");
            }
        }
    }

    public static class CostApiHealthReader
    {
        public static async Task<CostApiConnectionHealth> ReadGraphAsync(AppConfig config, CancellationToken ct,
            Func<ICostDocumentStore> open = null)
        {
            if (config?.CopilotCostConnectionEnabled != true || config.ImportJobSettings?.CopilotCostManagement != true)
                return Build("graph", "disabled", null);
            try
            {
                var store = open == null ? CostCollectorRuntime.CreateDocumentStore(config) : open();
                var document = await store.ReadAsync(CostConnectionService.Key, ct).ConfigureAwait(false);
                var state = document == null ? new CostConnectionState() : CostConnectionEncoding.Deserialize<CostConnectionState>(document.Content);
                var status = state.Status == "connected" && (state.AccountId == null || state.Cache == null)
                    ? "reconnectRequired" : state.Status;
                return Build("graph", status, state.CollectionHealth);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Build("graph", "unavailable", null);
            }
        }

        public static async Task<CostApiConnectionHealth> ReadStudioAsync(AppConfig config,
            Func<AgentCostConnectionStore> open = null)
        {
            if (config?.ImportJobSettings?.CopilotStudioCredits != true) return Build("studio", "disabled", null);
            try
            {
                var store = open == null ? AgentCostConnectionStore.TryOpen(config) : open();
                // No store only ever means no Storage connection string, which is a supported setup (the importer then
                // runs capacity-only): nothing can be connected. It is not a failure to read health, which would be
                // Unhealthy and raise the importer's Sev-2 alert. The Administration page already says "disconnected".
                if (store == null) return Build("studio", "disconnected", null);
                var head = await store.GetHeadAsync().ConfigureAwait(false);
                if (head?.Connected != true) return Build("studio", "disconnected", null);
                var reconnect = await store.NeedsReconnectAsync(head.Version).ConfigureAwait(false)
                    || await store.GetCacheAsync(head.Version).ConfigureAwait(false) == null;
                return Build("studio", reconnect ? "reconnectRequired" : "connected",
                    await store.GetHealthAsync(head.Version).ConfigureAwait(false));
            }
            catch (Exception)
            {
                return Build("studio", "unavailable", null);
            }
        }

        public static CostApiConnectionHealth Build(string source, string connectionStatus, CostApiHealth health)
        {
            var status = connectionStatus;
            if (status == "connected")
            {
                status = health?.FailureCode == null
                    ? health?.LastSuccessfulRead == null ? "pending" : "healthy"
                    : health.FailureCode == "reconnectRequired" || health.FailureCode == "permissionDenied"
                        ? health.FailureCode : "failing";
            }
            return new CostApiConnectionHealth
            {
                Source = source, Status = status,
                LastAttempt = health?.LastAttempt, LastSuccessfulRead = health?.LastSuccessfulRead,
                FailureSince = health?.FailureSince,
                FailureCode = health?.FailureCode == null ? null : CostApiHealth.SafeCode(health.FailureCode)
            };
        }
    }
}

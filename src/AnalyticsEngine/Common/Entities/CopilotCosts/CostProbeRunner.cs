using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotCosts
{
    public sealed class CostProbeRunner
    {
        private readonly ICostDocumentStore _store;
        private readonly HttpClient _http;
        private readonly Func<CancellationToken, Task> _assertAccess;
        public CostProbeRunner(ICostDocumentStore store, HttpClient http, Func<CancellationToken, Task> assertAccess = null)
        { _store = store; _http = http; _assertAccess = assertAccess; }

        public async Task StepAsync(CostConnectionState state, string delegatedToken, string directoryToken, CancellationToken ct)
        {
            if (state.Probe == null) state.Probe = new CostProbeProgress();
            var progress = state.Probe;
            progress.LastAttempt = DateTimeOffset.UtcNow;
            var source = new CostGraphSource(_http, delegatedToken, assertAccess: _assertAccess);
            try
            {
                if (progress.Phase == "context") await ReadContextAsync(progress, source, ct);
                else if (progress.Phase == "inventory")
                    await ReadInventoryAsync(progress, new CostGraphSource(_http, directoryToken, assertAccess: _assertAccess), ct);
                else if (progress.Phase == "users") await ReadUsersAsync(progress, source, ct);
                progress.LastOutcome = "sliceCompleted";
            }
            catch (CostConnectionException ex)
            {
                progress.LastOutcome = ex.Status;
                if (ex.Status == "permissionDenied" || ex.Status == "reconnectRequired") throw;
            }
        }

        private async Task ReadContextAsync(CostProbeProgress p, CostGraphSource source, CancellationToken ct)
        {
            if (p.ContextQueue.Count == 0) { p.Phase = "inventory"; return; }
            if (p.ContextReads >= 5000 || p.ContextQueue.Count > 5000) throw new CostConnectionException("pageLimit");
            var url = p.ContextQueue[0];
            var hash = CostConnectionEncoding.Hash(url);
            if (p.SeenLinks.Contains(hash)) throw new CostConnectionException("invalidPage");
            var json = await source.ReadAsync(url, "/beta/copilot/costManagement/", ct);
            var additions = new List<string>();
            var uri = new Uri(url);
            if (!uri.AbsolutePath.EndsWith("/getTenantCreditBalance", StringComparison.Ordinal))
            {
                if (!(json["value"] is JArray rows)) throw new CostConnectionException("invalidResponse");
                if (uri.AbsolutePath.EndsWith("/spendingPolicies", StringComparison.Ordinal))
                    foreach (var row in rows)
                    {
                        if (!(row is JObject) || !Guid.TryParse(CostGraphSource.Text(row["id"]), out var id)) throw new CostConnectionException("invalidResponse");
                        var root = "https://graph.microsoft.com/beta/copilot/costManagement/spendingPolicies/" + id;
                        additions.Add(root + "/serviceSettings");
                        if (CostGraphSource.Text(row["@odata.type"]) == "#microsoft.graph.selectedGroupsSpendingPolicy")
                            additions.Add(root + "/microsoft.graph.selectedGroupsSpendingPolicy/assignedGroups");
                    }
                if (json["@odata.nextLink"] != null)
                    additions.Insert(0, CostGraphSource.ValidateUrl(CostGraphSource.Text(json["@odata.nextLink"]), uri.AbsolutePath).AbsoluteUri);
            }
            await WriteEvidenceAsync("context_" + p.Id + "_" + p.ContextReads, json, ct);
            p.ContextQueue.RemoveAt(0);
            p.ContextQueue.InsertRange(0, additions);
            p.SeenLinks.Add(hash);
            p.ContextReads++;
        }

        private async Task ReadInventoryAsync(CostProbeProgress p, CostGraphSource source, CancellationToken ct)
        {
            if (p.InventoryPages >= 5000) throw new CostConnectionException("pageLimit");
            var hash = CostConnectionEncoding.Hash(p.NextLink);
            if (p.SeenLinks.Contains(hash)) throw new CostConnectionException("invalidPage");
            var key = "inventory_" + p.Id + "_" + p.InventoryPages;
            var existing = await _store.ReadAsync(key, ct);
            var json = existing == null
                ? await source.ReadAsync(p.NextLink, "/v1.0/users", ct)
                : CostConnectionEncoding.Deserialize<JObject>(existing.Content);
            if (!(json["value"] is JArray rows) || rows.Count > 999) throw new CostConnectionException("invalidResponse");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!(row is JObject) || !Guid.TryParse(CostGraphSource.Text(row["id"]), out var id)) throw new CostConnectionException("invalidResponse");
                ids.Add(id.ToString("N"));
            }
            if (existing == null) await WriteEvidenceAsync(key, json, ct);
            var pageIds = ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
            var end = Math.Min(pageIds.Count, p.InventoryOffset + 25);
            while (p.InventoryOffset < end)
            {
                var id = pageIds[p.InventoryOffset];
                var memberKey = "member_" + p.Id + "_" + id;
                var member = await _store.ReadAsync(memberKey, ct);
                if (member == null)
                    await _store.WriteAsync(memberKey, CostConnectionEncoding.Serialize(p.InventoryPages), null, ct);
                if (member == null || CostConnectionEncoding.Deserialize<int>(member.Content) == p.InventoryPages)
                    p.InventorySelected.Add(id);
                p.InventoryOffset++;
            }
            if (p.InventoryOffset < pageIds.Count) return;
            await WriteEvidenceAsync("page_" + p.Id + "_" + p.InventoryPages, p.InventorySelected, ct);
            var next = CostGraphSource.Text(json["@odata.nextLink"]);
            if (next != null) CostGraphSource.ValidateUrl(next, "/v1.0/users");
            p.UserCount += p.InventorySelected.Count;
            p.InventoryPages++;
            p.InventoryOffset = 0;
            p.InventorySelected.Clear();
            p.SeenLinks.Add(hash);
            p.NextLink = next;
            if (next == null) p.Phase = p.UserCount == 0 ? "observed" : "users";
        }

        private async Task ReadUsersAsync(CostProbeProgress p, CostGraphSource source, CancellationToken ct)
        {
            var doc = await _store.ReadAsync("page_" + p.Id + "_" + p.Page, ct);
            if (doc == null) throw new CostConnectionException("storageCorrupt");
            var ids = CostConnectionEncoding.Deserialize<List<string>>(doc.Content);
            var end = Math.Min(ids.Count, p.Offset + 5);
            while (p.Offset < end)
            {
                var id = ids[p.Offset];
                var key = "observation_" + p.Id + "_" + id;
                var prior = await _store.ReadAsync(key, ct);
                ProbeUserResult result;
                if (prior != null) result = CostConnectionEncoding.Deserialize<ProbeUserResult>(prior.Content);
                else
                {
                    try { result = new ProbeUserResult { Observation = await source.ReadUserAsync(id, ct) }; }
                    catch (CostConnectionException ex) when (ex.Status != "reconnectRequired" && ex.Status != "permissionDenied")
                    {
                        // Throttling/outages remain retryable at the same offset. A security-trimmed
                        // 404 is counted as denied/missing, never as an empty observation.
                        if (ex.Status != "notFound" && ex.Status != "invalidResponse" && ex.Status != "invalidPage") throw;
                        result = new ProbeUserResult { Failure = ex.Status };
                    }
                    await _store.WriteAsync(key, CostConnectionEncoding.Serialize(result), null, ct);
                }
                if (result.Failure != null)
                {
                    p.Failed++;
                    if (result.Failure == "notFound") p.Denied++;
                }
                else
                {
                    p.Observed++;
                    if (result.Observation.Services.Any(s => s.ConsumedQuantity.HasValue)) p.Measured++;
                    if (result.Observation.Services.Count == 0 && result.Observation.UnevaluatedServices.Count == 0) p.Empty++;
                    if (result.Observation.UnevaluatedServices.Count > 0)
                    {
                        p.Unevaluated++;
                        if (result.Observation.Services.Count > 0) p.Partial++;
                    }
                }
                p.Offset++;
            }
            if (p.Offset == ids.Count)
            {
                p.Page++;
                p.Offset = 0;
                if (p.Page == p.InventoryPages) p.Phase = "observed";
            }
        }

        private async Task WriteEvidenceAsync<T>(string key, T value, CancellationToken ct)
        {
            var prior = await _store.ReadAsync(key, ct);
            if (prior == null) await _store.WriteAsync(key, CostConnectionEncoding.Serialize(value), null, ct);
        }

        public sealed class ProbeUserResult
        {
            public CostUserObservation Observation { get; set; }
            public string Failure { get; set; }
        }
    }
}

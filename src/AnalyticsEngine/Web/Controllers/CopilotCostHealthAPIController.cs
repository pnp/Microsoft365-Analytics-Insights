using Common.Entities.Config;
using Common.Entities.CopilotCosts;
using System;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [Route("api/CopilotCostHealth")]
    public sealed class CopilotCostHealthAPIController : ControllerBase
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);
        private const string CacheKey = "copilot-cost-api-health";
        private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(15);
        private static Task<CostApiConnectionHealth> _pendingGraph;
        private static Task<CostApiConnectionHealth> _pendingStudio;

        [HttpGet, Route("")]
        public async Task<IActionResult> Get()
        {
            var result = MemoryCache.Default[CacheKey] as CostApiConnectionHealth[];
            if (result != null) return Ok(result);
            await Gate.WaitAsync();
            try
            {
                result = MemoryCache.Default[CacheKey] as CostApiConnectionHealth[];
                if (result == null)
                {
                    // Retain a slow read across requests: a non-cancellable Table call must not
                    // accumulate new background work every time the short cache expires.
                    var config = new AppConfig();
                    if (_pendingGraph == null) _pendingGraph = Guarded("graph", () => ReadGraphAsync(config));
                    if (_pendingStudio == null) _pendingStudio = Guarded("studio", () => CostApiHealthReader.ReadStudioAsync(config));
                    result = await CombineAsync(_pendingGraph, _pendingStudio, ReadDeadline);
                    if (_pendingGraph.IsCompleted) _pendingGraph = null;
                    if (_pendingStudio.IsCompleted) _pendingStudio = null;
                    MemoryCache.Default.Set(CacheKey, result, DateTimeOffset.UtcNow.AddSeconds(30));
                }
                return Ok(result);
            }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// Waits up to the deadline for both reads, then reports each source on its own. A source that is switched off
        /// answers at once, and the portal keeps it quiet; it must not become "needs attention" because the other
        /// source's read is slow or failed. Only a read that has not finished by the deadline, or did not succeed, is
        /// unavailable.
        /// </summary>
        internal static async Task<CostApiConnectionHealth[]> CombineAsync(Task<CostApiConnectionHealth> graph,
            Task<CostApiConnectionHealth> studio, TimeSpan deadline)
        {
            await Task.WhenAny(Task.WhenAll(graph, studio), Task.Delay(deadline));
            return new[] { Outcome(graph, "graph"), Outcome(studio, "studio") };
        }

        private static CostApiConnectionHealth Outcome(Task<CostApiConnectionHealth> read, string source) =>
            read.Status == TaskStatus.RanToCompletion ? read.Result : CostApiHealthReader.Build(source, "unavailable", null);

        private static async Task<CostApiConnectionHealth> ReadGraphAsync(AppConfig config)
        {
            using (var timeout = new CancellationTokenSource(ReadDeadline))
                return await CostApiHealthReader.ReadGraphAsync(config, timeout.Token);
        }

        /// <summary>A read that never faults: whatever stops it, its own source is unavailable - and only that source.</summary>
        private static async Task<CostApiConnectionHealth> Guarded(string source, Func<Task<CostApiConnectionHealth>> read)
        {
            try { return await read(); }
            catch (Exception) { return CostApiHealthReader.Build(source, "unavailable", null); }
        }
    }
}

using Common.Entities.Config;
using Common.Entities.CopilotCosts;
using System;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RoutePrefix("api/CopilotCostHealth")]
    public sealed class CopilotCostHealthAPIController : ApiController
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1);
        private const string CacheKey = "copilot-cost-api-health";
        private static Task<CostApiConnectionHealth[]> _pending;

        [HttpGet, Route("")]
        public async Task<IHttpActionResult> Get()
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
                    if (_pending == null) _pending = LoadAsync();
                    var completed = await Task.WhenAny(_pending, Task.Delay(TimeSpan.FromSeconds(15)));
                    result = completed == _pending ? await _pending : Unavailable();
                    if (_pending.IsCompleted) _pending = null;
                    MemoryCache.Default.Set(CacheKey, result, DateTimeOffset.UtcNow.AddSeconds(30));
                }
                return Ok(result);
            }
            finally { Gate.Release(); }
        }

        private static async Task<CostApiConnectionHealth[]> LoadAsync()
        {
            try
            {
                var config = new AppConfig();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    return await Task.WhenAll(CostApiHealthReader.ReadGraphAsync(config, timeout.Token),
                        CostApiHealthReader.ReadStudioAsync(config));
            }
            catch (Exception) { return Unavailable(); }
        }

        private static CostApiConnectionHealth[] Unavailable() => new[]
        {
            CostApiHealthReader.Build("graph", "unavailable", null),
            CostApiHealthReader.Build("studio", "unavailable", null)
        };
    }
}

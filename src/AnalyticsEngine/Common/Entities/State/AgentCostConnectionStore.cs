using Common.Entities.Config;
using Common.Entities.CopilotCosts;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// One tenant-wide billing connection. Refresh writers only touch their generation's cache, never the head:
    /// an in-flight refresh cannot reconnect a disconnected account or replace a newer connection.
    /// </summary>
    public sealed class AgentCostConnectionStore
    {
        public const string HeadKey = "Connection";
        private readonly IKeyValueStore _store;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(90);

        public AgentCostConnectionStore(IKeyValueStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public static AgentCostConnectionStore TryOpen(AppConfig config)
        {
            var store = StateStore.TryOpen(config, StatePartitions.AgentCostDelegatedAuth);
            return store == null ? null : new AgentCostConnectionStore(store);
        }

        public async Task<AgentCostConnectionHead> GetHeadAsync()
        {
            var value = await _store.GetStringAsync(HeadKey).ConfigureAwait(false);
            return value == null ? null : JsonConvert.DeserializeObject<AgentCostConnectionHead>(value);
        }

        public async Task<string> GetStatusAsync()
        {
            var head = await GetHeadAsync().ConfigureAwait(false);
            if (head?.Connected != true) return "disconnected";
            if (await _store.ExistsAsync(ErrorKey(head.Version)).ConfigureAwait(false)
                || !await _store.ExistsAsync(CacheKey(head.Version)).ConfigureAwait(false))
                return "reconnectNeeded";
            return "connected";
        }

        public async Task ConnectAsync(string encryptedCache)
        {
            var old = await GetHeadAsync().ConfigureAwait(false);
            var health = old?.Connected == true ? await GetHealthAsync(old.Version).ConfigureAwait(false) : null;
            if (old?.Connected == true && await NeedsReconnectAsync(old.Version).ConfigureAwait(false))
            {
                health = health ?? new CostApiHealth();
                health.RecordFailure("reconnectRequired", DateTimeOffset.UtcNow);
            }
            var head = new AgentCostConnectionHead { Version = Guid.NewGuid().ToString("N"), Connected = true };
            await SaveCacheAsync(head.Version, encryptedCache).ConfigureAwait(false);
            if (health != null)
                await _store.SetStringAsync("Health_" + head.Version, JsonConvert.SerializeObject(health), CacheLifetime).ConfigureAwait(false);
            await _store.SetStringAsync(HeadKey, JsonConvert.SerializeObject(head)).ConfigureAwait(false);
        }

        public async Task DisconnectAsync()
        {
            var old = await GetHeadAsync().ConfigureAwait(false);
            await _store.SetStringAsync(HeadKey, JsonConvert.SerializeObject(new AgentCostConnectionHead
            {
                Version = Guid.NewGuid().ToString("N"), Connected = false,
            })).ConfigureAwait(false);
            if (old != null)
            {
                await _store.DeleteAsync(CacheKey(old.Version)).ConfigureAwait(false);
                await _store.DeleteAsync(ErrorKey(old.Version)).ConfigureAwait(false);
            }
        }

        public Task<string> GetCacheAsync(string version) => _store.GetStringAsync(CacheKey(version));
        public Task SaveCacheAsync(string version, string value) =>
            _store.SetStringAsync(CacheKey(version), value, CacheLifetime);
        public Task RequireReconnectAsync(string version) =>
            _store.SetStringAsync(ErrorKey(version), "reconnectNeeded", CacheLifetime);
        public Task<bool> NeedsReconnectAsync(string version) => _store.ExistsAsync(ErrorKey(version));

        public async Task<CostApiHealth> GetHealthAsync(string version)
        {
            var value = await _store.GetStringAsync("Health_" + version).ConfigureAwait(false);
            return value == null ? null : JsonConvert.DeserializeObject<CostApiHealth>(value);
        }

        public async Task RecordHealthAsync(string version, string failureCode, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(version)) return;
            var health = await GetHealthAsync(version).ConfigureAwait(false) ?? new CostApiHealth();
            health.LastAttempt = now;
            if (failureCode == null) health.RecordSuccess(now);
            else health.RecordFailure(failureCode, now);
            // Like the token cache, health belongs to a connection generation, never to the head.
            await _store.SetStringAsync("Health_" + version, JsonConvert.SerializeObject(health), CacheLifetime).ConfigureAwait(false);
        }

        private static string CacheKey(string version) => "Cache_" + version;
        private static string ErrorKey(string version) => "Error_" + version;
    }

    public sealed class AgentCostConnectionHead
    {
        public string Version { get; set; }
        public bool Connected { get; set; }
    }
}

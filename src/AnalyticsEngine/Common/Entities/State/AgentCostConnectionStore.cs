using Common.Entities.Config;
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
            var head = new AgentCostConnectionHead { Version = Guid.NewGuid().ToString("N"), Connected = true };
            await SaveCacheAsync(head.Version, encryptedCache).ConfigureAwait(false);
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

        private static string CacheKey(string version) => "Cache_" + version;
        private static string ErrorKey(string version) => "Error_" + version;
    }

    public sealed class AgentCostConnectionHead
    {
        public string Version { get; set; }
        public bool Connected { get; set; }
    }
}

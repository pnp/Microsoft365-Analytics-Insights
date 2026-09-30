using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Email
{
    /// <summary>
    /// Interface for per-user delta token storage.
    /// </summary>
    public interface IDeltaTokenStore
    {
        Task<string> GetDeltaToken(string key);
        Task SetDeltaToken(string key, string deltaToken);
    }

    /// <summary>
    /// In-memory delta token store. Useful for tests and single-process runs. Backed by a
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> because the sent-email importer's
    /// parallel Graph loader calls Get/Set concurrently per user.
    /// </summary>
    public class InMemoryDeltaTokenStore : IDeltaTokenStore
    {
        private readonly ConcurrentDictionary<string, string> _tokens = new ConcurrentDictionary<string, string>();

        public Task<string> GetDeltaToken(string key)
        {
            _tokens.TryGetValue(key, out var token);
            return Task.FromResult(token);
        }

        public Task SetDeltaToken(string key, string deltaToken)
        {
            _tokens[key] = deltaToken;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Durable per-user delta token store over the runtime state store (the <see cref="Common.Entities.State.StatePartitions.SentEmails"/>
    /// partition of the state table), one row per mailbox.
    /// </summary>
    public class PersistedDeltaTokenStore : IDeltaTokenStore
    {
        private readonly Common.Entities.State.IKeyValueStore _store;

        public PersistedDeltaTokenStore(Common.Entities.State.IKeyValueStore store)
        {
            _store = store ?? throw new System.ArgumentNullException(nameof(store));
        }

        public async Task<string> GetDeltaToken(string key)
        {
            return await _store.GetStringAsync(key);
        }

        public async Task SetDeltaToken(string key, string deltaToken)
        {
            await _store.SetStringAsync(key, deltaToken);
        }
    }
}

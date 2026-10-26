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

        /// <summary>
        /// Forget the value stored under <paramref name="key"/>, so the next read is a full one. Used when Graph has
        /// rejected the token: replaying it can never succeed.
        /// </summary>
        Task ClearDeltaToken(string key);
    }

    /// <summary>
    /// An <see cref="IDeltaTokenStore"/> that can write many tokens in one round trip, so the sent-email import's checkpoints
    /// for a chunk of mailboxes are one storage call instead of one per mailbox.
    /// </summary>
    public interface IBatchDeltaTokenStore : IDeltaTokenStore
    {
        /// <summary>Stores every value under its key. Throws when it could not all be written; repeating the call is safe.</summary>
        Task SetDeltaTokens(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, string>> tokens);
    }

    /// <summary>
    /// In-memory delta token store. Useful for tests and single-process runs. Backed by a
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> because the sent-email importer's
    /// parallel Graph loader calls Get/Set concurrently per user.
    /// </summary>
    public class InMemoryDeltaTokenStore : IBatchDeltaTokenStore
    {
        private readonly ConcurrentDictionary<string, string> _tokens = new ConcurrentDictionary<string, string>();

        /// <summary>How many tokens are held.</summary>
        public int Count => _tokens.Count;

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

        public Task SetDeltaTokens(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, string>> tokens)
        {
            foreach (var token in tokens)
            {
                _tokens[token.Key] = token.Value;
            }
            return Task.CompletedTask;
        }

        public Task ClearDeltaToken(string key)
        {
            _tokens.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Durable per-user delta token store over the runtime state store (the <see cref="Common.Entities.State.StatePartitions.SentEmails"/>
    /// partition of the state table), one row per mailbox.
    /// </summary>
    public class PersistedDeltaTokenStore : IBatchDeltaTokenStore
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

        public async Task SetDeltaTokens(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, string>> tokens)
        {
            if (_store is Common.Entities.State.IBatchKeyValueStore batch)
            {
                await batch.SetManyAsync(tokens);
                return;
            }

            foreach (var token in tokens)
            {
                await _store.SetStringAsync(token.Key, token.Value);
            }
        }

        public async Task ClearDeltaToken(string key)
        {
            await _store.DeleteAsync(key);
        }
    }
}

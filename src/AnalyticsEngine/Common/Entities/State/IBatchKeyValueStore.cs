using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// An <see cref="IKeyValueStore"/> that can write many keys of its partition in one round trip. For a caller that would
    /// otherwise await one write after another - the sent-email import's checkpoints, one per mailbox - which at ~10 ms each is
    /// over half an hour of a 200,000-mailbox cycle.
    /// </summary>
    public interface IBatchKeyValueStore : IKeyValueStore
    {
        /// <summary>
        /// Stores every value (none may be <c>null</c>, and none gets a time-to-live), replacing what is there. Groups of up to
        /// 100 are written atomically, so a failure can leave earlier groups written: repeating the call is safe. When a key
        /// appears more than once the last value wins. Throws when the store cannot be reached.
        /// </summary>
        Task SetManyAsync(IReadOnlyList<KeyValuePair<string, string>> values, CancellationToken cancellationToken = default);
    }
}

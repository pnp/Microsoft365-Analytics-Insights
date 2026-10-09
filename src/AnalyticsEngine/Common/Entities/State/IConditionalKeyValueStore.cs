using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>A stored value with the version token (an Azure Table ETag) it was read at.</summary>
    public sealed class VersionedValue
    {
        public VersionedValue(string value, string versionToken)
        {
            Value = value;
            VersionToken = versionToken;
        }

        /// <summary>The value, or <c>null</c> when the key does not exist or has expired.</summary>
        public string Value { get; }

        /// <summary>
        /// Identifies the row as read. <c>null</c> only when no row exists at all; an expired row still has one, so a
        /// conditional write can replace it.
        /// </summary>
        public string VersionToken { get; }
    }

    /// <summary>
    /// An <see cref="IKeyValueStore"/> that also offers optimistic concurrency: a write that lands only when the row is
    /// still exactly as it was read. Used where two writers must never both succeed against the same predecessor, such
    /// as the administrator-edited Copilot Adoption score settings and their audit trail.
    /// </summary>
    public interface IConditionalKeyValueStore : IKeyValueStore
    {
        /// <summary>The value and its version token. Never null; throws when the store cannot be reached.</summary>
        Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes <paramref name="value"/> (with no time-to-live) only when the row still carries
        /// <paramref name="expectedVersionToken"/>, or - when that is <c>null</c> - only when no row exists yet.
        /// Returns false, writing nothing, when another writer got there first. Throws when the store cannot be reached.
        /// </summary>
        Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default);
    }
}

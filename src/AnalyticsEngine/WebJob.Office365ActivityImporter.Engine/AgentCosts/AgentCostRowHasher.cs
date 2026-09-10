using System;
using System.Security.Cryptography;
using System.Text;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Builds the fixed-width natural key used to upsert agent-cost fact rows.
    ///
    /// Both sources restate history - Azure re-estimates an open billing period several times a day, and
    /// Copilot Studio consumption is recalculated as usage days settle - so both importers re-read a trailing
    /// window. That only works if a re-read row can be matched to the one already stored, and the identifying
    /// dimensions are collectively far wider than SQL Server's 1700-byte index-key limit (they are Unicode, so
    /// 2 bytes per character). Hashing them to 64 hex characters makes the natural key indexable.
    /// </summary>
    public static class AgentCostRowHasher
    {
        /// <summary>
        /// Separator between hashed components. Chosen as a unit separator rather than something like "|"
        /// because it cannot occur in a meter name, resource id or agent name, so two different dimension
        /// tuples cannot collide by concatenating to the same string.
        /// </summary>
        private const char Separator = '\u001F';

        /// <summary>
        /// Hex SHA-256 of the given components, in order. A null component is distinguished from an empty one,
        /// so ("a", null) and ("a", "") do not collide.
        /// </summary>
        public static string Hash(params string[] components)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));

            var sb = new StringBuilder();
            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0) sb.Append(Separator);

                // A null is encoded as a marker that an empty string cannot produce. Without this, a row with
                // no LLM model and a row with an empty-string LLM model would hash identically and the second
                // would silently overwrite the first.
                sb.Append(components[i] == null ? "\u0000" : components[i]);
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    hex.Append(b.ToString("x2"));
                }
                return hex.ToString();
            }
        }
    }
}

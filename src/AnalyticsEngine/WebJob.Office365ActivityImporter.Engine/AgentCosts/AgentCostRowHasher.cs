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
    ///
    /// <para><b>Callers format the usage date in the invariant culture.</b> Before that, the host culture was
    /// used, and the change is not expected to have altered any stored key. "yyyy-MM-dd" has no culture-dependent
    /// separator, so on any host whose calendar is Gregorian - including Azure App Service, whose default culture
    /// is en-US - the old and new strings are identical. Only a host whose default calendar is not Gregorian
    /// (th-TH, ar-SA and the Persian-calendar cultures such as fa-IR) hashed a different date string, and that
    /// host also sent its REQUEST dates in that calendar,
    /// asking for data in years such as 2569 (th-TH) or 1405 (fa-IR); a row could only have been stored under the
    /// old key if an API had answered such a request with data anyway. Were that ever to happen, the Azure import would converge
    /// by itself (its replace removes any row in the refresh window the new result does not contain), but the
    /// Copilot Studio upserts never delete, so a re-read day would be stored twice: same usage date and
    /// dimensions, two <c>dimension_hash</c> values.</para>
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

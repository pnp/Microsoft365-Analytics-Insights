using System;
using System.Text.RegularExpressions;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// The installer's minimum-TLS rule for every resource that has one: raise a minimum below TLS 1.2 (or no minimum at
    /// all) to 1.2, and leave 1.2 or anything stricter exactly as it is.
    /// </summary>
    /// <remarks>
    /// The tasks used to test "is it 1.2?" rather than "is it below 1.2?", so a re-run put a resource an operator had
    /// raised to TLS 1.3 back to 1.2 while logging "enforce TLS 1.2". Each resource provider spells the value its own way
    /// ("1.2" for App Service, Service Bus and SQL, "TLS1_2" for Storage, "None" for a SQL server with no minimum), so the
    /// comparison is on the parsed version, never the string.
    /// </remarks>
    public static class TlsVersionPolicy
    {
        public static readonly Version RequiredMinimum = new Version(1, 2);

        private static readonly Regex VersionPattern = new Regex(@"^(?:TLS)?\s*(\d+)[._]?(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True when the installer should raise <paramref name="current"/> to TLS 1.2: it is unset, "None", or a version below
        /// 1.2. A value the installer does not recognise is left alone, because it may be stricter than anything known here.
        /// </summary>
        public static bool NeedsRaising(string current)
        {
            if (string.IsNullOrWhiteSpace(current) || string.Equals(current.Trim(), "None", StringComparison.OrdinalIgnoreCase))
                return true;

            var parsed = TryParse(current);
            return parsed != null && parsed < RequiredMinimum;
        }

        /// <summary>Parses "1.2", "TLS1_2", "Tls12" and the like. Null for anything else.</summary>
        public static Version TryParse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = VersionPattern.Match(value.Trim());
            if (!match.Success) return null;
            return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
    }
}

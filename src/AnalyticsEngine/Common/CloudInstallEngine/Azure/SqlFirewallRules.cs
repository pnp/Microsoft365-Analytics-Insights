using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Pure helpers for reasoning about Azure SQL firewall rules and the client IP Azure actually sees.
    /// </summary>
    /// <remarks>
    /// Deliberately free of any network or ARM dependency so every decision here is unit-testable. The task
    /// that calls them (<c>SqlServerFirewallConfigTask</c>) keeps the I/O.
    /// </remarks>
    public static class SqlFirewallRules
    {
        /// <summary>
        /// The "allow all Azure services" rule. Azure represents it as the sentinel range 0.0.0.0-0.0.0.0,
        /// which covers Azure-internal callers, NOT client IPs - so it must never be counted as covering the
        /// installer host, or a stale rule would look fine forever.
        /// </summary>
        public const string AzureServicesSentinelIp = "0.0.0.0";

        /// <summary>
        /// Azure SQL error number for "Client with IP address '...' is not allowed to access the server".
        /// </summary>
        public const int ClientIpNotAllowedErrorNumber = 40615;

        /// <summary>
        /// Whether <paramref name="clientIp"/> falls inside any of <paramref name="rules"/>.
        /// </summary>
        /// <remarks>
        /// Compares addresses as unsigned 32-bit values, not strings: '9.9.9.9' &lt;= '10.0.0.1' is false as a
        /// string comparison and true numerically, so a string compare would silently mis-answer whole ranges.
        /// The Azure-services sentinel range is ignored - see <see cref="AzureServicesSentinelIp"/>.
        /// </remarks>
        public static bool IsIpCoveredByAnyRule(string clientIp, IEnumerable<SqlFirewallRuleRange> rules)
        {
            if (!TryParseIPv4(clientIp, out var client)) return false;
            if (rules == null) return false;

            return rules.Any(r => RuleCovers(r, client));
        }

        /// <summary>The rules that cover <paramref name="clientIp"/>, for reporting which one is doing so.</summary>
        public static IReadOnlyList<SqlFirewallRuleRange> RulesCovering(string clientIp, IEnumerable<SqlFirewallRuleRange> rules)
        {
            if (!TryParseIPv4(clientIp, out var client) || rules == null)
            {
                return new List<SqlFirewallRuleRange>();
            }

            return rules.Where(r => RuleCovers(r, client)).ToList();
        }

        private static bool RuleCovers(SqlFirewallRuleRange rule, uint client)
        {
            if (rule == null) return false;

            // The Azure-services sentinel is not a client-IP range.
            if (rule.StartIp == AzureServicesSentinelIp && rule.EndIp == AzureServicesSentinelIp) return false;

            if (!TryParseIPv4(rule.StartIp, out var start)) return false;
            if (!TryParseIPv4(rule.EndIp, out var end)) return false;

            // Tolerate a rule stored back-to-front rather than silently treating it as covering nothing.
            if (start > end)
            {
                var swap = start;
                start = end;
                end = swap;
            }

            return client >= start && client <= end;
        }

        /// <summary>
        /// Strict IPv4 parse to an unsigned 32-bit value, big-endian so numeric ordering matches dotted-quad
        /// ordering.
        /// </summary>
        /// <remarks>
        /// Hand-rolled rather than delegating to <see cref="IPAddress.TryParse"/>, which on .NET Framework
        /// still accepts legacy forms: "10" is 0.0.0.10, "1.2.3" is 1.2.0.3, "1.2.3.010" is 1.2.3.8 (octal)
        /// and "1.2.3.0x10" is 1.2.3.16 (hex). Those values would then be echoed back verbatim into an ARM
        /// firewall rule, where they mean something different from what was validated here. Only canonical
        /// dotted-quad decimal is accepted.
        /// </remarks>
        public static bool TryParseIPv4(string value, out uint address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var parts = value.Trim().Split('.');
            if (parts.Length != 4) return false;

            uint result = 0;
            foreach (var part in parts)
            {
                // 1-3 ASCII digits, and no leading zero (which would be octal in the legacy forms).
                if (part.Length < 1 || part.Length > 3) return false;
                if (part.Length > 1 && part[0] == '0') return false;

                var octet = 0;
                foreach (var c in part)
                {
                    if (c < '0' || c > '9') return false;
                    octet = (octet * 10) + (c - '0');
                }

                if (octet > 255) return false;
                result = (result << 8) | (uint)octet;
            }

            address = result;
            return true;
        }

        /// <summary>Whether the value is a well-formed IPv4 address (and therefore usable in a firewall rule).</summary>
        public static bool IsValidIPv4(string value) => TryParseIPv4(value, out _);

        /// <summary>
        /// Whether the installer may safely overwrite <paramref name="existingRule"/> with a single-address
        /// range.
        /// </summary>
        /// <remarks>
        /// The installer owns this rule by name, but an admin may well have widened it to a corporate range.
        /// Replacing that with one address would silently revoke access for every other address it covered, so
        /// a multi-address rule is left alone and reported instead. A rule that is absent, a single address, or
        /// has unparseable bounds is ours to rewrite.
        /// </remarks>
        public static bool CanSafelyReplaceWithSingleAddress(SqlFirewallRuleRange existingRule)
        {
            if (existingRule == null) return true;

            if (!TryParseIPv4(existingRule.StartIp, out var start)) return true;
            if (!TryParseIPv4(existingRule.EndIp, out var end)) return true;

            return start == end;
        }

        // Azure's message: "Cannot open server '<server>' requested by the login. Client with IP address
        // '203.0.113.10' is not allowed to access the server." Anchored on the quoted address that follows the
        // phrase, so an address appearing elsewhere in a wrapped message cannot be picked up by mistake.
        private static readonly Regex _blockedIpPattern = new Regex(
            @"[Cc]lient with IP address\s+'(?<ip>[0-9a-fA-F\.:]+)'",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Pulls the client IP out of an Azure SQL "not allowed to access the server" message.
        /// </summary>
        /// <remarks>
        /// This is the authoritative answer to "what address does THIS server see me as" - better than any
        /// echo service, which reports the egress IP of the connection to the echo service. Those differ under
        /// split-tunnel VPNs, proxies and multi-address NAT pools, so an echo service can be confidently wrong
        /// and we would then write a wrong rule.
        /// </remarks>
        public static bool TryGetBlockedClientIp(string sqlErrorMessage, out string clientIp)
        {
            clientIp = null;
            if (string.IsNullOrWhiteSpace(sqlErrorMessage)) return false;

            var match = _blockedIpPattern.Match(sqlErrorMessage);
            if (!match.Success) return false;

            var candidate = match.Groups["ip"].Value;

            // Azure SQL firewall rules are IPv4-only; an IPv6 client cannot be repaired by writing a rule.
            if (!IsValidIPv4(candidate)) return false;

            clientIp = candidate;
            return true;
        }
    }

    /// <summary>
    /// One Azure SQL firewall rule reduced to the three things that matter here, so the coverage logic can be
    /// tested without an ARM <c>SqlFirewallRuleResource</c>.
    /// </summary>
    public class SqlFirewallRuleRange
    {
        public SqlFirewallRuleRange(string name, string startIp, string endIp)
        {
            Name = name;
            StartIp = startIp;
            EndIp = endIp;
        }

        public string Name { get; }
        public string StartIp { get; }
        public string EndIp { get; }

        public override string ToString() => $"'{Name}' ({StartIp} - {EndIp})";
    }
}

using CloudInstallEngine.Azure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// The SQL firewall self-heal logic (issue #326).
    /// </summary>
    /// <remarks>
    /// The installer decided by RULE NAME alone: if "O365 Adv Analytics Setup Rule" existed it logged
    /// "already present ... Skipping" without ever reading the IP range it held. So a stale rule - after a
    /// DHCP renewal, a different network, or VPN on/off - meant the install reported success here and died
    /// two minutes later at the database step, by which point the App Service had already been stopped.
    /// </remarks>
    [TestClass]
    public class SqlFirewallRulesTests
    {
        private static SqlFirewallRuleRange Rule(string name, string start, string end) => new SqlFirewallRuleRange(name, start, end);

        private static List<SqlFirewallRuleRange> Rules(params SqlFirewallRuleRange[] rules) => rules.ToList();

        #region IP coverage

        [TestMethod]
        public void AnIpInsideARangeIsCovered()
        {
            var rules = Rules(Rule("Corp", "203.0.113.0", "203.0.113.255"));

            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", rules));
        }

        [TestMethod]
        public void RangeBoundariesAreInclusive()
        {
            var rules = Rules(Rule("Corp", "203.0.113.10", "203.0.113.20"));

            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", rules), "Start of range must be covered.");
            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.20", rules), "End of range must be covered.");
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.9", rules));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.21", rules));
        }

        [TestMethod]
        public void AStaleSingleAddressRuleDoesNotCoverADifferentAddress()
        {
            // The exact #326 scenario: the rule exists under the right name but points at the old IP.
            var rules = Rules(Rule("O365 Adv Analytics Setup Rule", "198.51.100.5", "198.51.100.5"));

            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", rules),
                "A rule with the right NAME but the wrong IP must not count as covering us.");
        }

        [TestMethod]
        public void AddressesAreComparedNumericallyNotAsStrings()
        {
            // '9.9.9.9' <= '10.0.0.1' is FALSE as a string comparison and TRUE numerically, so a string
            // compare would mis-answer whole ranges. Same for the octet boundary at 99/100.
            var rules = Rules(Rule("Wide", "9.0.0.0", "10.255.255.255"));

            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("9.9.9.9", rules));
            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("10.0.0.1", rules));

            var octetRules = Rules(Rule("Octet", "203.0.113.99", "203.0.113.200"));
            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.100", octetRules),
                "'203.0.113.100' sorts before '203.0.113.99' as a string but is numerically inside the range.");
        }

        [TestMethod]
        public void TheAzureServicesSentinelRuleNeverCoversAClientIp()
        {
            // 0.0.0.0-0.0.0.0 means "allow Azure-internal callers", not "allow everyone". Counting it would
            // make every stale rule look fine for ever.
            var rules = Rules(Rule("AllowAllWindowsAzureIps", "0.0.0.0", "0.0.0.0"));

            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", rules));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("0.0.0.0", rules));
        }

        [TestMethod]
        public void AnAdminAddedRangeCountsAsCoverage()
        {
            // The question is "is my IP allowed?", not "does my named rule exist?" - a corporate range is a
            // perfectly good reason to leave the installer's own rule alone.
            var rules = Rules(
                Rule("AllowAllWindowsAzureIps", "0.0.0.0", "0.0.0.0"),
                Rule("O365 Adv Analytics Setup Rule", "198.51.100.5", "198.51.100.5"),
                Rule("Corporate egress", "203.0.113.0", "203.0.113.255"));

            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", rules));

            var covering = SqlFirewallRules.RulesCovering("203.0.113.10", rules);
            Assert.AreEqual(1, covering.Count);
            Assert.AreEqual("Corporate egress", covering.Single().Name);
        }

        [TestMethod]
        public void ARuleStoredBackToFrontIsStillEvaluated()
        {
            var rules = Rules(Rule("Reversed", "203.0.113.255", "203.0.113.0"));

            Assert.IsTrue(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", rules),
                "A reversed range should be tolerated rather than silently covering nothing.");
        }

        [TestMethod]
        public void MalformedOrMissingInputsAreNotCoverage()
        {
            var rules = Rules(Rule("Corp", "203.0.113.0", "203.0.113.255"));

            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule(null, rules));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("", rules));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("not-an-ip", rules));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", null));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", Rules()));

            // A rule with unparseable bounds must not throw, and must not count as covering anything.
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", Rules(Rule("Bad", "junk", "junk"))));
            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("203.0.113.10", Rules(Rule("Null", null, null))));
        }

        [TestMethod]
        public void IPv6IsNotTreatedAsAnIPv4ClientAddress()
        {
            // Azure SQL firewall rules are IPv4-only, so an IPv6 address can never be "covered".
            var rules = Rules(Rule("Corp", "0.0.0.1", "255.255.255.255"));

            Assert.IsFalse(SqlFirewallRules.IsIpCoveredByAnyRule("2001:db8::1", rules));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("2001:db8::1"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("::1"));
        }

        [TestMethod]
        public void ShorthandFormsAreRejectedAsIPv4()
        {
            // IPAddress.TryParse alone accepts these as IPv4 (e.g. "10" => 0.0.0.10), which would write a
            // nonsense firewall rule.
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("10"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3.4.5"));
            Assert.IsTrue(SqlFirewallRules.IsValidIPv4("1.2.3.4"));
            Assert.IsTrue(SqlFirewallRules.IsValidIPv4("  203.0.113.10  "), "Surrounding whitespace should be tolerated.");
        }

        [TestMethod]
        public void LegacyOctalAndHexFormsAreRejected()
        {
            // .NET Framework's IPAddress.TryParse reads "1.2.3.010" as 1.2.3.8 (octal) and "1.2.3.0x10" as
            // 1.2.3.16 (hex). Accepting those would validate one address and then echo the original text into
            // an ARM firewall rule, where it may mean something else entirely.
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3.010"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3.0x10"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("010.2.3.4"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3.256"), "Octets above 255 are not addresses.");
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3.-4"));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3."));
            Assert.IsFalse(SqlFirewallRules.IsValidIPv4("1.2.3.4 5"));

            // A single zero octet is canonical and must still be accepted.
            Assert.IsTrue(SqlFirewallRules.IsValidIPv4("10.0.0.1"));
            Assert.IsTrue(SqlFirewallRules.IsValidIPv4("0.0.0.0"));
            Assert.IsTrue(SqlFirewallRules.IsValidIPv4("255.255.255.255"));
        }

        [TestMethod]
        public void OrderingIsPreservedAcrossOctetBoundaries()
        {
            Assert.IsTrue(SqlFirewallRules.TryParseIPv4("0.0.0.0", out var min));
            Assert.IsTrue(SqlFirewallRules.TryParseIPv4("255.255.255.255", out var max));
            Assert.IsTrue(SqlFirewallRules.TryParseIPv4("0.0.1.0", out var justOverAnOctet));
            Assert.IsTrue(SqlFirewallRules.TryParseIPv4("0.0.0.255", out var topOfFirstOctet));

            Assert.AreEqual(0u, min);
            Assert.AreEqual(uint.MaxValue, max);
            Assert.IsTrue(topOfFirstOctet < justOverAnOctet, "Parsing must be big-endian for ordering to work.");
        }

        #endregion

        #region Not narrowing a rule an admin widened

        [TestMethod]
        public void ASingleAddressRuleCanBeReplaced()
        {
            Assert.IsTrue(SqlFirewallRules.CanSafelyReplaceWithSingleAddress(
                Rule("O365 Adv Analytics Setup Rule", "198.51.100.5", "198.51.100.5")));
        }

        [TestMethod]
        public void AMissingRuleCanBeCreated()
        {
            Assert.IsTrue(SqlFirewallRules.CanSafelyReplaceWithSingleAddress(null));
        }

        [TestMethod]
        public void AnAdminWidenedRangeIsNotOverwritten()
        {
            // The installer owns this rule by name, but an admin may well have widened it to a corporate
            // range. Narrowing it back to one address would silently revoke access for every other address it
            // covers - worse than the problem being fixed.
            Assert.IsFalse(SqlFirewallRules.CanSafelyReplaceWithSingleAddress(
                Rule("O365 Adv Analytics Setup Rule", "203.0.113.0", "203.0.113.255")));
        }

        [TestMethod]
        public void ARuleWithUnparseableBoundsIsOursToRewrite()
        {
            // Nothing useful is being protected, so repairing it is strictly an improvement.
            Assert.IsTrue(SqlFirewallRules.CanSafelyReplaceWithSingleAddress(Rule("Broken", "junk", "junk")));
            Assert.IsTrue(SqlFirewallRules.CanSafelyReplaceWithSingleAddress(Rule("Broken", null, null)));
        }

        #endregion

        #region Reading the blocked IP out of Azure's own error

        [TestMethod]
        public void TheBlockedClientIpIsReadFromAzuresRejectionMessage()
        {
            // This is the authoritative answer to "what address does THIS server see me as" - better than any
            // echo service, which reports the egress IP of the connection to the echo service.
            const string message =
                "Cannot open server 'contosoanalytics' requested by the login. Client with IP address " +
                "'203.0.113.10' is not allowed to access the server. To enable access, use the Windows Azure " +
                "Management Portal or run sp_set_firewall_rule on the master database...";

            Assert.IsTrue(SqlFirewallRules.TryGetBlockedClientIp(message, out var ip));
            Assert.AreEqual("203.0.113.10", ip);
        }

        [TestMethod]
        public void TheBlockedClientIpIsFoundInAWrappedMessage()
        {
            const string message =
                "Error testing SQL connection to 'contosoanalytics.database.windows.net': 'Cannot open server " +
                "'contosoanalytics' requested by the login. Client with IP address '198.51.100.7' is not allowed " +
                "to access the server.'. Verify network connectivity to server.";

            Assert.IsTrue(SqlFirewallRules.TryGetBlockedClientIp(message, out var ip));
            Assert.AreEqual("198.51.100.7", ip);
        }

        [TestMethod]
        public void AnIPv6ClientCannotBeRepairedByAFirewallRule()
        {
            // Azure SQL firewall rules are IPv4-only. Reporting a repair we cannot perform would be worse than
            // falling through to the normal error.
            const string message = "Client with IP address '2001:db8::1' is not allowed to access the server.";

            Assert.IsFalse(SqlFirewallRules.TryGetBlockedClientIp(message, out var ip));
            Assert.IsNull(ip);
        }

        [TestMethod]
        public void AnUnrelatedMessageYieldsNoIp()
        {
            Assert.IsFalse(SqlFirewallRules.TryGetBlockedClientIp(null, out _));
            Assert.IsFalse(SqlFirewallRules.TryGetBlockedClientIp("", out _));
            Assert.IsFalse(SqlFirewallRules.TryGetBlockedClientIp("Login failed for user 'sqladmin'.", out _));

            // An IP-shaped string elsewhere in the message must not be mistaken for the blocked client. The old
            // detection regex was unanchored and used IsMatch, so any IP-shaped substring passed.
            Assert.IsFalse(
                SqlFirewallRules.TryGetBlockedClientIp("A network-related error occurred contacting 10.1.2.3.", out _));
        }

        [TestMethod]
        public void TheErrorNumberMatchesAzuresDocumentedFirewallRejection()
        {
            Assert.AreEqual(40615, SqlFirewallRules.ClientIpNotAllowedErrorNumber);
        }

        #endregion
    }

    /// <summary>
    /// Public-IP detection hardening (issue #326). The old code read
    /// <c>http://icanhazip.com</c> over plain HTTP with <c>WebClient</c> - no timeout, no exception handling -
    /// and validated the response with an UNANCHORED regex plus <c>IsMatch</c>, so any IP-shaped text in a
    /// captive-portal or error page passed and got written into a customer's SQL firewall.
    /// </summary>
    [TestClass]
    public class PublicIpResolverTests
    {
        [TestMethod]
        public void TheEchoServiceIsCalledOverHttps()
        {
            StringAssert.StartsWith(PublicIpResolver.EchoServiceUrl, "https://",
                "This value decides what goes into a customer's SQL firewall; it must not travel over plain HTTP.");
        }

        [TestMethod]
        public void RequestsAreBounded()
        {
            Assert.IsTrue(PublicIpResolver.RequestTimeout > System.TimeSpan.Zero);
            Assert.IsTrue(PublicIpResolver.RequestTimeout <= System.TimeSpan.FromSeconds(30),
                "A hung endpoint must not be able to stall the install.");
        }

        [TestMethod]
        public void AnEchoBodyIsAcceptedOnlyWhenItIsAnAddressAndNothingElse()
        {
            Assert.AreEqual("203.0.113.10", PublicIpResolver.ParseEchoServiceBody("203.0.113.10"));
            Assert.AreEqual("203.0.113.10", PublicIpResolver.ParseEchoServiceBody("203.0.113.10\n"));
            Assert.AreEqual("203.0.113.10", PublicIpResolver.ParseEchoServiceBody("  203.0.113.10\r\n"));
        }

        [TestMethod]
        public void AnErrorOrCaptivePortalPageIsRejectedEvenIfItContainsAnIp()
        {
            // The exact failure mode the old unanchored IsMatch allowed.
            Assert.IsNull(PublicIpResolver.ParseEchoServiceBody(
                "<html><body>Sign in to continue. Gateway 192.168.0.1</body></html>"));
            Assert.IsNull(PublicIpResolver.ParseEchoServiceBody("Your IP is 203.0.113.10"));
            Assert.IsNull(PublicIpResolver.ParseEchoServiceBody("error"));
            Assert.IsNull(PublicIpResolver.ParseEchoServiceBody(""));
            Assert.IsNull(PublicIpResolver.ParseEchoServiceBody(null));
        }

        [TestMethod]
        public void AnIPv6EchoResponseIsRejected()
        {
            // Azure SQL firewall rules are IPv4-only, so an IPv6-only host must fall through to an explicit
            // failure rather than having a nonsense rule written for it.
            Assert.IsNull(PublicIpResolver.ParseEchoServiceBody("2001:db8::1"));
        }

        [TestMethod]
        public void TheCallerIpIsReadFromKeyVaultsNetworkInfoHeader()
        {
            // First-party and returned on the 401 of an UNAUTHENTICATED request, so it needs no token and no
            // vault firewall access - and unlike an echo service it is a Microsoft endpoint.
            Assert.AreEqual("203.0.113.10",
                PublicIpResolver.ParseKeyVaultNetworkInfo("conn_type=Ipv4;addr=203.0.113.10;act_addr_fam=InterNetwork;"));

            Assert.AreEqual("203.0.113.10",
                PublicIpResolver.ParseKeyVaultNetworkInfo("addr=203.0.113.10"));

            Assert.AreEqual("203.0.113.10",
                PublicIpResolver.ParseKeyVaultNetworkInfo("conn_type=Ipv4; ADDR = 203.0.113.10 ;x=y"));
        }

        [TestMethod]
        public void AMalformedOrIPv6KeyVaultHeaderYieldsNothing()
        {
            Assert.IsNull(PublicIpResolver.ParseKeyVaultNetworkInfo(null));
            Assert.IsNull(PublicIpResolver.ParseKeyVaultNetworkInfo(""));
            Assert.IsNull(PublicIpResolver.ParseKeyVaultNetworkInfo("conn_type=Ipv4;act_addr_fam=InterNetwork;"));
            Assert.IsNull(PublicIpResolver.ParseKeyVaultNetworkInfo("conn_type=Ipv6;addr=2001:db8::1;"));
            Assert.IsNull(PublicIpResolver.ParseKeyVaultNetworkInfo("addr=not-an-ip"));
        }
    }
}

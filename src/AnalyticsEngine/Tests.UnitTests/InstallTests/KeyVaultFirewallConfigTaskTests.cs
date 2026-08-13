using Azure;
using Azure.ResourceManager.KeyVault.Models;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Unit tests for the Key Vault firewall rule construction (issue #136). These cover the pure,
    /// Azure-free logic: parsing App Service outbound IPs and building the firewall rule set.
    /// </summary>
    [TestClass]
    public class KeyVaultFirewallConfigTaskTests
    {
        [TestMethod]
        public void ParseOutboundIPv4Addresses_TrimsDedupesAndIgnoresNonIPv4()
        {
            // Mixed: duplicates, whitespace, an IPv6 address, junk and a trailing blank.
            var input = "52.178.30.162, 52.178.27.13 ,52.178.30.162,not-an-ip,2001:db8::1,";

            var result = KeyVaultFirewallConfigTask.ParseOutboundIPv4Addresses(input);

            CollectionAssert.AreEqual(new[] { "52.178.30.162", "52.178.27.13" }, result);
        }

        [TestMethod]
        public void ParseOutboundIPv4Addresses_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(0, KeyVaultFirewallConfigTask.ParseOutboundIPv4Addresses(null).Count);
            Assert.AreEqual(0, KeyVaultFirewallConfigTask.ParseOutboundIPv4Addresses("   ").Count);
        }

        [TestMethod]
        public void BuildFirewallRuleSet_EnablesDenyFirewallAndAddsIps()
        {
            var ruleSet = KeyVaultFirewallConfigTask.BuildFirewallRuleSet(
                existing: null,
                allowIpAddresses: new[] { "5.159.8.251", "52.178.30.162" },
                vnetSubnetId: null);

            Assert.IsTrue(ruleSet.DefaultAction == KeyVaultNetworkRuleAction.Deny, "Firewall must default-deny.");
            Assert.IsTrue(ruleSet.Bypass == KeyVaultNetworkRuleBypassOption.AzureServices, "Trusted Azure services must be allowed to bypass.");
            CollectionAssert.AreEqual(new[] { "5.159.8.251", "52.178.30.162" }, ruleSet.IPRules.Select(r => r.AddressRange).ToList());
            Assert.AreEqual(0, ruleSet.VirtualNetworkRules.Count);
        }

        [TestMethod]
        public void BuildFirewallRuleSet_PreservesExistingRulesAndDeduplicates()
        {
            var existing = new KeyVaultNetworkRuleSet { DefaultAction = KeyVaultNetworkRuleAction.Allow };
            existing.IPRules.Add(new KeyVaultIPRule("5.159.8.251"));
            existing.VirtualNetworkRules.Add(new KeyVaultVirtualNetworkRule("/subscriptions/s/.../subnets/subnetA"));

            var ruleSet = KeyVaultFirewallConfigTask.BuildFirewallRuleSet(
                existing: existing,
                allowIpAddresses: new[] { "5.159.8.251", "9.9.9.9" },   // 5.159.8.251 already present -> deduped
                vnetSubnetId: "/subscriptions/s/.../subnets/subnetB");

            CollectionAssert.AreEqual(new[] { "5.159.8.251", "9.9.9.9" }, ruleSet.IPRules.Select(r => r.AddressRange).ToList());
            CollectionAssert.AreEqual(
                new[] { "/subscriptions/s/.../subnets/subnetA", "/subscriptions/s/.../subnets/subnetB" },
                ruleSet.VirtualNetworkRules.Select(r => r.Id.ToString()).ToList());
        }

        [TestMethod]
        public void BuildFirewallRuleSet_AddedVnetRuleIgnoresMissingServiceEndpoint()
        {
            var ruleSet = KeyVaultFirewallConfigTask.BuildFirewallRuleSet(
                existing: null,
                allowIpAddresses: null,
                vnetSubnetId: "/subscriptions/s/.../subnets/integration");

            Assert.AreEqual(1, ruleSet.VirtualNetworkRules.Count);
            Assert.AreEqual(true, ruleSet.VirtualNetworkRules.Single().IgnoreMissingVnetServiceEndpoint);
        }

        [TestMethod]
        public void IsDisallowedByPolicy_Detects403PolicyDenial()
        {
            // "Not allowed resource types" Azure Policy denial: a 403 the installer must
            // treat as best-effort (reuse the existing vault) rather than a fatal install error.
            var ex = new RequestFailedException(403, "Resource was disallowed by policy.", "RequestDisallowedByPolicy", null);
            Assert.IsTrue(KeyVaultTask.IsDisallowedByPolicy(ex));
        }

        [TestMethod]
        public void IsDisallowedByPolicy_IgnoresOther403sAndStatuses()
        {
            // A plain RBAC 403 (Forbidden) or any other status must NOT be swallowed as a policy denial.
            Assert.IsFalse(KeyVaultTask.IsDisallowedByPolicy(new RequestFailedException(403, "Forbidden", "Forbidden", null)));
            Assert.IsFalse(KeyVaultTask.IsDisallowedByPolicy(new RequestFailedException(409, "Conflict", "RequestDisallowedByPolicy", null)));
        }
    }
}

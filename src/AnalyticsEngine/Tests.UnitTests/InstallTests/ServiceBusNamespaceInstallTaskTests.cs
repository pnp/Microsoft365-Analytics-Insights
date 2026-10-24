using Azure.ResourceManager.ServiceBus.Models;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Guards the minimum-TLS decision in <see cref="ServiceBusNamespaceInstallTask"/>.
    ///
    /// The task previously compared against <c>ServiceBusMinimumTlsVersion.Tls1_2</c>, which in
    /// Azure.ResourceManager.ServiceBus 1.2.0 is a deprecated alias whose underlying value is the EMPTY
    /// STRING - the real API values live on <c>Tls10</c>..<c>Tls13</c>. The comparison therefore never
    /// matched the "1.2" the ARM API returns, so every install logged "Updating service-bus namespace
    /// '...' to enforce TLS 1.2 minimum..." and re-PUT the namespace (which also wiped its tags), while
    /// writing an empty minimumTlsVersion that enforced nothing.
    ///
    /// These tests are written against a value built the way the SDK's deserialiser builds it - from the
    /// raw API string - so they keep working across SDK upgrades and would fail again if the wrong
    /// constant were reintroduced.
    /// </summary>
    [TestClass]
    public class ServiceBusNamespaceInstallTaskTests
    {
        /// <summary>What Microsoft.ServiceBus returns in the namespace payload for TLS 1.2.</summary>
        private const string ARM_TLS_12 = "1.2";

        [TestMethod]
        public void RequiredMinimumTlsVersion_IsTheValueTheArmApiUses()
        {
            Assert.AreEqual(
                ARM_TLS_12,
                ServiceBusNamespaceInstallTask.RequiredMinimumTlsVersion.ToString(),
                "The constant sent as minimumTlsVersion must be the literal ARM value, not an empty deprecated alias.");
        }

        [TestMethod]
        public void NeedsMinimumTlsUpdate_NamespaceAlreadyAtTls12_ReturnsFalse()
        {
            var asReturnedByArm = new ServiceBusMinimumTlsVersion(ARM_TLS_12);

            Assert.IsFalse(
                ServiceBusNamespaceInstallTask.NeedsMinimumTlsUpdate(asReturnedByArm),
                "A namespace already at TLS 1.2 must not be rewritten - otherwise every install re-PUTs it.");
        }

        [TestMethod]
        public void NeedsMinimumTlsUpdate_UnsetOrOlderVersion_ReturnsTrue()
        {
            Assert.IsTrue(
                ServiceBusNamespaceInstallTask.NeedsMinimumTlsUpdate(null),
                "A namespace with no minimum TLS version must be raised to 1.2.");
            Assert.IsTrue(
                ServiceBusNamespaceInstallTask.NeedsMinimumTlsUpdate(new ServiceBusMinimumTlsVersion("1.0")),
                "A namespace on TLS 1.0 must be raised to 1.2.");
            Assert.IsTrue(
                ServiceBusNamespaceInstallTask.NeedsMinimumTlsUpdate(new ServiceBusMinimumTlsVersion("1.1")),
                "A namespace on TLS 1.1 must be raised to 1.2.");
        }

        /// <summary>
        /// E-8: the calls queue was created with Azure's defaults and never updated. On the Basic tier that is a 14-day time
        /// to live with expired messages dropped, so an importer outage longer than that lost every queued call notification.
        /// An existing queue as ARM returns it must be updated - dead-lettering switched on, the time to live kept at the
        /// tier's maximum - and a second pass must find nothing to change.
        /// </summary>
        [TestMethod]
        public void QueueSettings_ABasicTierQueueWithAzureDefaults_GetsExpiryDeadLettering_AndASecondPassChangesNothing()
        {
            var asReturnedByArm = new Azure.ResourceManager.ServiceBus.ServiceBusQueueData
            {
                DefaultMessageTimeToLive = System.TimeSpan.FromDays(14),
                DeadLetteringOnMessageExpiration = false,
                LockDuration = System.TimeSpan.FromMinutes(1),
            };

            var changes = ServiceBusQueueSettings.Apply(asReturnedByArm, ServiceBusSkuName.Basic);

            CollectionAssert.AreEqual(new[] { "dead-lettering on message expiration off -> on" }, changes.ToArray());
            Assert.AreEqual(true, asReturnedByArm.DeadLetteringOnMessageExpiration);
            Assert.AreEqual(System.TimeSpan.FromDays(14), asReturnedByArm.DefaultMessageTimeToLive, "14 days is Basic's maximum: ARM refuses more.");
            Assert.AreEqual(System.TimeSpan.FromMinutes(1), asReturnedByArm.LockDuration, "The queue's other settings are sent back as they were.");
            Assert.AreEqual(0, ServiceBusQueueSettings.Apply(asReturnedByArm, ServiceBusSkuName.Basic).Count, "Idempotent: a reinstall rewrites nothing.");
        }

        /// <summary>On Standard and Premium the maximum is unlimited; a lowered time to live is raised back, and a new queue gets it explicitly.</summary>
        [TestMethod]
        public void QueueSettings_StandardAndPremium_GetUnlimitedTimeToLive_UnknownTierGetsAValueEveryTierAccepts()
        {
            var lowered = new Azure.ResourceManager.ServiceBus.ServiceBusQueueData { DefaultMessageTimeToLive = System.TimeSpan.FromDays(14), DeadLetteringOnMessageExpiration = true };
            var changes = ServiceBusQueueSettings.Apply(lowered, ServiceBusSkuName.Standard);
            Assert.AreEqual(System.TimeSpan.MaxValue, lowered.DefaultMessageTimeToLive);
            CollectionAssert.AreEqual(new[] { "default message time to live 14 days -> unlimited" }, changes.ToArray());

            var fresh = new Azure.ResourceManager.ServiceBus.ServiceBusQueueData();
            Assert.AreEqual(2, ServiceBusQueueSettings.Apply(fresh, ServiceBusSkuName.Premium).Count);
            Assert.AreEqual(System.TimeSpan.MaxValue, fresh.DefaultMessageTimeToLive);
            Assert.AreEqual(true, fresh.DeadLetteringOnMessageExpiration);

            var unknownTier = new Azure.ResourceManager.ServiceBus.ServiceBusQueueData();
            ServiceBusQueueSettings.Apply(unknownTier, null);
            Assert.AreEqual(ServiceBusQueueSettings.BasicTierMaxTimeToLive, unknownTier.DefaultMessageTimeToLive);
        }
    }
}

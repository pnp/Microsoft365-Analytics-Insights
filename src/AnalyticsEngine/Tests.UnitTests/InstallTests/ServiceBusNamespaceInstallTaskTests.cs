using Azure.ResourceManager.ServiceBus.Models;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    }
}

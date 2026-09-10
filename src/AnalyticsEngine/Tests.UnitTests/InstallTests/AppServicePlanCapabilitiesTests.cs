using App.ControlPanel.Engine;
using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService.Models;
using ManagedServiceIdentityType = Azure.ResourceManager.Models.ManagedServiceIdentityType;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class AppServicePlanCapabilitiesTests
    {
        [DataTestMethod]
        [DataRow("Free", "F1", false)]
        [DataRow("Shared", "D1", false)]
        [DataRow("Basic", "B1", true)]
        [DataRow("Standard", "S1", true)]
        [DataRow("PremiumV3", "P1V3", true)]
        public void SupportsAlwaysOn_UsesTierCapabilityBoundary(string tier, string name, bool expected)
        {
            Assert.AreEqual(expected, AppServicePlanCapabilities.SupportsAlwaysOn(Sku(tier, name)));
        }

        [TestMethod]
        public void SupportsAlwaysOn_NullEmptyAndUnknown_DefaultsToSupported()
        {
            Assert.IsTrue(AppServicePlanCapabilities.SupportsAlwaysOn(null));
            Assert.IsTrue(AppServicePlanCapabilities.SupportsAlwaysOn(Sku(null, null)));
            Assert.IsTrue(AppServicePlanCapabilities.SupportsAlwaysOn(Sku("ElasticPremium", "EP1")));
        }

        [TestMethod]
        public void SupportsAlwaysOn_FallsBackToNameWhenTierIsMissing()
        {
            Assert.IsFalse(AppServicePlanCapabilities.SupportsAlwaysOn(Sku(null, "F1")));
            Assert.IsFalse(AppServicePlanCapabilities.SupportsAlwaysOn(Sku(null, "D1")));
        }

        [TestMethod]
        public void BuildSecureSiteConfig_FreePlanSkipsAlwaysOnButKeepsSecurityHardening()
        {
            var config = AppServiceWebsiteTask.BuildSecureSiteConfig(Sku("Free", "F1"));

            Assert.IsNull(config.IsAlwaysOn, "Always On must not be sent to Free/Shared plans.");
            Assert.AreEqual(AppServiceFtpsState.Disabled, config.FtpsState, "FTPS hardening must still be applied.");
            Assert.AreEqual(AppServiceSupportedTlsVersion.Tls1_2, config.MinTlsVersion, "TLS 1.2 hardening must still be applied.");
        }

        [TestMethod]
        public void BuildSecureSiteConfig_BasicPlanEnablesAlwaysOnAndSecurityHardening()
        {
            var config = AppServiceWebsiteTask.BuildSecureSiteConfig(Sku("Basic", "B1"));

            Assert.AreEqual(true, config.IsAlwaysOn, "Always On must still be enabled on B1+ plans.");
            Assert.AreEqual(AppServiceFtpsState.Disabled, config.FtpsState);
            Assert.AreEqual(AppServiceSupportedTlsVersion.Tls1_2, config.MinTlsVersion);
        }


        [TestMethod]
        public void BuildNewWebSiteData_FreePlanSkipsAlwaysOnButKeepsPublicAccessAndIdentityHardening()
        {
            var data = AppServiceWebsiteTask.BuildNewWebSiteData(
                AzureLocation.WestEurope,
                new ResourceIdentifier("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-rg/providers/Microsoft.Web/serverfarms/contoso-plan"),
                Sku("Free", "F1"),
                allowPublicAccess: false);

            Assert.AreEqual("Disabled", data.PublicNetworkAccess, "Public network access hardening must still be applied.");
            Assert.IsNotNull(data.Identity, "System-assigned managed identity must still be requested.");
            Assert.AreEqual(ManagedServiceIdentityType.SystemAssigned, data.Identity.ManagedServiceIdentityType);
            Assert.IsNull(data.SiteConfig.IsAlwaysOn, "Always On must not be sent to Free/Shared plans.");
            Assert.AreEqual(AppServiceFtpsState.Disabled, data.SiteConfig.FtpsState);
            Assert.AreEqual(AppServiceSupportedTlsVersion.Tls1_2, data.SiteConfig.MinTlsVersion);
        }

        [TestMethod]
        public void BuildPostCreateSiteConfig_FreePlanSkipsAlwaysOnAnd64BitWorkerSetting()
        {
            var config = ConfigureAzureComponentsTasks.BuildPostCreateSiteConfig(Sku("Shared", "D1"));

            Assert.IsNull(config.IsAlwaysOn, "Always On must not be sent to Free/Shared plans.");
            Assert.IsNull(config.Use32BitWorkerProcess, "64-bit worker setting must not be sent to Free/Shared plans.");
        }

        [TestMethod]
        public void BuildPostCreateSiteConfig_BasicPlanEnablesAlwaysOnAnd64BitWorkers()
        {
            var config = ConfigureAzureComponentsTasks.BuildPostCreateSiteConfig(Sku("Basic", "B1"));

            Assert.AreEqual(true, config.IsAlwaysOn, "Always On must still be enabled on B1+ plans.");
            Assert.AreEqual(false, config.Use32BitWorkerProcess, "B1+ plans should still be set to 64-bit workers.");
        }

        [TestMethod]
        public void UnsupportedWarning_NamesPlanTierConsequenceAndRemedy()
        {
            var warning = AppServicePlanCapabilities.BuildAlwaysOnUnsupportedWarning("contoso-plan", Sku("Free", "F1"));

            StringAssert.Contains(warning, "contoso-plan");
            StringAssert.Contains(warning, "Free");
            StringAssert.Contains(warning, "site will be unloaded when idle");
            StringAssert.Contains(warning, "web-jobs will not run reliably");
            StringAssert.Contains(warning, "Scale the plan to B1 or higher and re-run the installer");
        }

        private static AppServiceSkuDescription Sku(string tier, string name)
        {
            return new AppServiceSkuDescription
            {
                Tier = tier,
                Name = name,
                Size = name
            };
        }
    }
}

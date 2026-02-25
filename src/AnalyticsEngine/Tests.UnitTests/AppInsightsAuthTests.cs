using Azure.Core;
using Azure.Identity;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    [TestClass]
    public class AppInsightsAuthTests
    {
        /// <summary>
        /// Verify AppInsightsAPIClient requires a non-null/empty connection string.
        /// </summary>
        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void AppInsightsAPIClient_NullConnectionString_Throws()
        {
            var credential = new ClientSecretCredential("tenant", "client", "secret");
            new AppInsightsAPIClient(null, credential, AnalyticsLogger.ConsoleOnlyTracer());
        }

        /// <summary>
        /// Verify AppInsightsAPIClient requires a non-empty connection string.
        /// </summary>
        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void AppInsightsAPIClient_EmptyConnectionString_Throws()
        {
            var credential = new ClientSecretCredential("tenant", "client", "secret");
            new AppInsightsAPIClient(string.Empty, credential, AnalyticsLogger.ConsoleOnlyTracer());
        }

        /// <summary>
        /// Verify AppInsightsAPIClient requires a non-null credential.
        /// </summary>
        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void AppInsightsAPIClient_NullCredential_Throws()
        {
            new AppInsightsAPIClient("InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://test.in.applicationinsights.azure.com/", null, AnalyticsLogger.ConsoleOnlyTracer());
        }

        /// <summary>
        /// Verify AppInsightsAPIClient rejects a connection string without an InstrumentationKey.
        /// </summary>
        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void AppInsightsAPIClient_ConnectionStringWithoutIKey_Throws()
        {
            var credential = new ClientSecretCredential("tenant-id", "client-id", "secret");
            new AppInsightsAPIClient("IngestionEndpoint=https://test.in.applicationinsights.azure.com/", credential, AnalyticsLogger.ConsoleOnlyTracer());
        }

        /// <summary>
        /// Verify AppInsightsAPIClient can be constructed with a valid connection string.
        /// </summary>
        [TestMethod]
        public void AppInsightsAPIClient_ValidConnectionString_Constructs()
        {
            var credential = new ClientSecretCredential("tenant-id", "client-id", "secret");
            var connStr = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://test.in.applicationinsights.azure.com/;LiveEndpoint=https://test.livediagnostics.monitor.azure.com/";
            using (var client = new AppInsightsAPIClient(connStr, credential, AnalyticsLogger.ConsoleOnlyTracer()))
            {
                Assert.IsNotNull(client);
            }
        }

        /// <summary>
        /// Verify InstrumentationKey is correctly parsed from a standard connection string.
        /// </summary>
        [TestMethod]
        public void ParseInstrumentationKey_ValidConnectionString()
        {
            var connStr = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://eastus-0.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/";
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", AppInsightsAPIClient.ParseInstrumentationKey(connStr));
        }

        /// <summary>
        /// Verify ParseInstrumentationKey returns null when connection string is empty.
        /// </summary>
        [TestMethod]
        public void ParseInstrumentationKey_NullOrEmpty_ReturnsNull()
        {
            Assert.IsNull(AppInsightsAPIClient.ParseInstrumentationKey(null));
            Assert.IsNull(AppInsightsAPIClient.ParseInstrumentationKey(string.Empty));
        }

        /// <summary>
        /// Verify ParseInstrumentationKey returns null when key is missing from connection string.
        /// </summary>
        [TestMethod]
        public void ParseInstrumentationKey_MissingKey_ReturnsNull()
        {
            var connStr = "IngestionEndpoint=https://eastus-0.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/";
            Assert.IsNull(AppInsightsAPIClient.ParseInstrumentationKey(connStr));
        }

        /// <summary>
        /// Verify AppConfig no longer has AppInsightsApiKey, AppInsightsAppId, or AppInsightsInstrumentationKey properties.
        /// These properties were removed as part of the migration to Entra ID authentication.
        /// </summary>
        [TestMethod]
        public void AppConfig_OldApiKeyProperties_Removed()
        {
            var configType = typeof(AppConfig);
            Assert.IsNull(configType.GetProperty("AppInsightsApiKey"), "AppInsightsApiKey should no longer exist on AppConfig");
            Assert.IsNull(configType.GetProperty("AppInsightsAppId"), "AppInsightsAppId should no longer exist on AppConfig");
            Assert.IsNull(configType.GetProperty("AppInsightsInstrumentationKey"), "AppInsightsInstrumentationKey should no longer exist on AppConfig");
        }
    }
}

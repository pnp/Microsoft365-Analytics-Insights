using Microsoft.VisualStudio.TestTools.UnitTesting;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for the App Insights connection-string parsing helper.
    /// Mirrors the parser used by ImportConfigController.ParseInstrumentationKey
    /// (B10 fix): the earlier authentication check extracted "the first GUID
    /// found by regex", which matched whichever key appeared first textually
    /// and could be defeated by reordering the connection-string segments.
    /// Parsing by key name is order-independent.
    /// </summary>
    [TestClass]
    public class AppInsightsConnectionStringParserTests
    {
        // Two-GUID App Insights connection strings; same values, different ordering.
        private const string InstrumentationKey = "11111111-1111-1111-1111-111111111111";
        private const string ApplicationId = "22222222-2222-2222-2222-222222222222";

        private static string InstrumentationKeyFirst() =>
            $"InstrumentationKey={InstrumentationKey};IngestionEndpoint=https://example/;ApplicationId={ApplicationId}";

        private static string ApplicationIdFirst() =>
            $"ApplicationId={ApplicationId};IngestionEndpoint=https://example/;InstrumentationKey={InstrumentationKey}";

        [TestMethod]
        public void ParseConnectionStringValue_InstrumentationKey_ReturnsCorrectValueRegardlessOfPosition()
        {
            Assert.AreEqual(InstrumentationKey,
                AppInsightsAPIClient.ParseConnectionStringValue(InstrumentationKeyFirst(), "InstrumentationKey"));
            Assert.AreEqual(InstrumentationKey,
                AppInsightsAPIClient.ParseConnectionStringValue(ApplicationIdFirst(), "InstrumentationKey"),
                "Parser must locate InstrumentationKey by name, not by position - position-based matching was the bug.");
        }

        [TestMethod]
        public void ParseConnectionStringValue_ApplicationId_ReturnsCorrectValueRegardlessOfPosition()
        {
            Assert.AreEqual(ApplicationId,
                AppInsightsAPIClient.ParseConnectionStringValue(InstrumentationKeyFirst(), "ApplicationId"));
            Assert.AreEqual(ApplicationId,
                AppInsightsAPIClient.ParseConnectionStringValue(ApplicationIdFirst(), "ApplicationId"));
        }

        [TestMethod]
        public void ParseConnectionStringValue_KeyLookupIsCaseInsensitive()
        {
            // Defensive: keys in real connection strings often vary in case
            Assert.AreEqual(InstrumentationKey,
                AppInsightsAPIClient.ParseConnectionStringValue(InstrumentationKeyFirst(), "instrumentationkey"));
        }

        [TestMethod]
        public void ParseConnectionStringValue_MissingKey_ReturnsNull()
        {
            Assert.IsNull(
                AppInsightsAPIClient.ParseConnectionStringValue(InstrumentationKeyFirst(), "DoesNotExist"));
        }

        [TestMethod]
        public void ParseConnectionStringValue_NullOrEmpty_ReturnsNull()
        {
            Assert.IsNull(AppInsightsAPIClient.ParseConnectionStringValue(null, "InstrumentationKey"));
            Assert.IsNull(AppInsightsAPIClient.ParseConnectionStringValue(string.Empty, "InstrumentationKey"));
        }

        [TestMethod]
        public void ParseConnectionStringValue_IgnoresWhitespaceAroundKeyAndValue()
        {
            var cs = $"  InstrumentationKey  =  {InstrumentationKey}  ;  ApplicationId  =  {ApplicationId}";
            Assert.AreEqual(InstrumentationKey,
                AppInsightsAPIClient.ParseConnectionStringValue(cs, "InstrumentationKey"));
        }
    }
}

using Common.Entities.Config;
using System;
using System.Text;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Get the import config for the client-side AITracker.
    /// Protected by CORs and a GUID in the AppInsights connection string.
    /// </summary>
    public class ImportConfigController : ApiController
    {
        // Get App Insights import config
        // POST: api/ImportConfig?appInsightsStringEncoded=base64encodedstring
        [AllowCorsForOrgUrls()]
        [HttpPost]
        public ImportConfig Post(string appInsightsStringEncoded = "")
        {
            if (string.IsNullOrEmpty(appInsightsStringEncoded))
                throw new ArgumentNullException("appInsightsStringEncoded");

            // Decode the base64 encoded string
            var bytes = Convert.FromBase64String(appInsightsStringEncoded);
            var decodedString = Encoding.UTF8.GetString(bytes);

            // The App Insights connection string contains multiple GUIDs (InstrumentationKey and
            // ApplicationId). Compare the InstrumentationKey value parsed by name from both sides,
            // not "the first GUID found" - the prior regex-by-position approach matched whichever
            // GUID appeared first textually and could be tricked by reordering the keys.
            var config = new AppConfig();
            var paramPassedKey = ParseInstrumentationKey(decodedString);
            var configuredKey = ParseInstrumentationKey(config.AppInsightsConnectionString);

            if (paramPassedKey == Guid.Empty)
                throw new ArgumentException("InstrumentationKey missing or invalid in supplied connection string");
            if (configuredKey == Guid.Empty)
                throw new InvalidOperationException("Server-side InstrumentationKey is not configured");

            if (paramPassedKey != configuredKey)
                throw new UnauthorizedAccessException("InstrumentationKey mismatch");

            return new ImportConfig
            {
                Expiry = DateTime.UtcNow.AddMinutes(config.MetadataRefreshMinutes),
                MetadataRefreshMinutes = config.MetadataRefreshMinutes,
            };
        }

        internal static Guid ParseInstrumentationKey(string connectionString)
        {
            var raw = ParseConnectionStringValue(connectionString, "InstrumentationKey");
            if (string.IsNullOrWhiteSpace(raw)) return Guid.Empty;
            return Guid.TryParse(raw, out var g) ? g : Guid.Empty;
        }

        /// <summary>
        /// Parses a named value from a semicolon-delimited connection string (e.g. App Insights).
        /// Duplicated from AppInsightsAPIClient.ParseConnectionStringValue to avoid adding a
        /// cross-project dependency on WebJob.AppInsightsImporter.Engine for a 12-line helper.
        /// </summary>
        internal static string ParseConnectionStringValue(string connectionString, string keyName)
        {
            if (string.IsNullOrEmpty(connectionString)) return null;
            foreach (var part in connectionString.Split(';'))
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = part.Substring(0, separatorIndex).Trim();
                    var value = part.Substring(separatorIndex + 1).Trim();
                    if (key.Equals(keyName, StringComparison.OrdinalIgnoreCase))
                        return value;
                }
            }
            return null;
        }
    }
}

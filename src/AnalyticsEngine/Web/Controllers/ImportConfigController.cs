using Common.Entities.Config;
using System;
using System.Text;
using System.Text.RegularExpressions;
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

            // Match to app insights instrumentation key
            // It's a bit of a hack, as there's usually two GUIDs in the connection string.
            // But it's _a_ way of checking the client-side is sending to the right server-side.
            var config = new AppConfig();
            var paramPassedGuid = FindGuidInString(decodedString);
            var configuredGuid = FindGuidInString(config.AppInsightsConnectionString);

            if (paramPassedGuid != configuredGuid)
                throw new UnauthorizedAccessException("Invalid GUID");

            return new ImportConfig
            {
                Expiry = DateTime.Now.AddMinutes(config.MetadataRefreshMinutes),
                MetadataRefreshMinutes = config.MetadataRefreshMinutes,
            };
        }

        Guid FindGuidInString(string s)
        {
            // Extract the GUID using regex
            const string pattern = @"[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12}";
            var match = Regex.Match(s, pattern);
            if (!match.Success)
                throw new ArgumentException("Invalid GUID");

            var guidString = match.Value;
            var guid = new Guid(guidString);

            return guid;
        }
    }
}

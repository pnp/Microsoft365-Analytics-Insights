using Common.Entities.Config;
using System;
using System.Text.RegularExpressions;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    public class ImportConfigController : ApiController
    {
        // Get App Insights import config
        // POST: api/ImportConfig?appInsightsStringEncoded=base64encodedstring
        [HttpPost]
        public ImportConfig Post(string appInsightsStringEncoded = "")
        {
            if (string.IsNullOrEmpty(appInsightsStringEncoded))
                throw new ArgumentNullException("appInsightsStringEncoded");

            // Extract the GUID using regex
            const string pattern = @"[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12}";
            var match = Regex.Match(appInsightsStringEncoded, pattern);
            if (!match.Success)
                throw new ArgumentException("Invalid GUID");

            var guidString = match.Value;
            var guid = new Guid(guidString);

            return new ImportConfig
            {
                Expiry = DateTime.Now.AddDays(1),
                MetadataRefreshMinutes = 60 * 24
            };
        }
    }
}

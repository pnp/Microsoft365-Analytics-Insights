using Common.Entities.Config;
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Http;
using System.Web.Http.Cors;

namespace Web.AnalyticsWeb.Controllers
{
    public class ImportConfigController : ApiController
    {
        // Get App Insights import config
        // POST: api/ImportConfig?appInsightsStringEncoded=base64encodedstring
        [EnableCors(origins: "*", headers: "*", methods: "post")]
        [HttpPost]
        public ImportConfig Post(string appInsightsStringEncoded = "")
        {
            if (string.IsNullOrEmpty(appInsightsStringEncoded))
                throw new ArgumentNullException("appInsightsStringEncoded");

            // Decode the base64 encoded string
            var bytes = Convert.FromBase64String(appInsightsStringEncoded);
            var decodedString = Encoding.UTF8.GetString(bytes);


            // Match to app insights instrumentation key
            var config = new AppConfig();
            var paramPassedGuid = FindGuidInString(decodedString);
            var configuredGuid = FindGuidInString(config.AppInsightsConnectionString);

            if (paramPassedGuid != configuredGuid)
                throw new UnauthorizedAccessException("Invalid GUID");

            return new ImportConfig
            {
                Expiry = DateTime.Now.AddDays(1),
                MetadataRefreshMinutes = 60 * 24
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

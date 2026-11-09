using Common.Entities.Config;
using System;
using System.Net;
using System.Net.Http;
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
        private readonly Func<(string AppInsightsConnectionString, int MetadataRefreshMinutes)> _settings;

        public ImportConfigController() : this(() =>
        {
            var config = new AppConfig();
            return (config.AppInsightsConnectionString, config.MetadataRefreshMinutes);
        })
        {
        }

        internal ImportConfigController(Func<(string AppInsightsConnectionString, int MetadataRefreshMinutes)> settings)
        {
            _settings = settings;
        }

        // Get App Insights import config
        // POST: api/ImportConfig?appInsightsStringEncoded=base64encodedstring
        /// <remarks>
        /// Anonymous, so whatever anyone posts is answered without an exception: malformed input is a 400 and another
        /// Application Insights resource's key a 403. Each used to be a 500 with an exception record in the customer's
        /// Application Insights, which crowded real faults off the Health page (#853). A server with no key configured is
        /// a real fault, and still throws.
        /// </remarks>
        [AllowCorsForOrgUrls()]
        [HttpPost]
        [AllowCrossSiteRequests("The AITracker posts here from SharePoint pages, another origin, without the portal's cookie.")]
        public IHttpActionResult Post(string appInsightsStringEncoded = "")
        {
            // The App Insights connection string contains multiple GUIDs (InstrumentationKey and
            // ApplicationId). Compare the InstrumentationKey value parsed by name from both sides,
            // not "the first GUID found" - the prior regex-by-position approach matched whichever
            // GUID appeared first textually and could be tricked by reordering the keys.
            var paramPassedKey = DecodeInstrumentationKey(appInsightsStringEncoded);
            if (paramPassedKey == Guid.Empty)
                return Refuse(HttpStatusCode.BadRequest, "Expected a base64-encoded Application Insights connection string with an InstrumentationKey.");

            var config = _settings();
            var configuredKey = ParseInstrumentationKey(config.AppInsightsConnectionString);
            if (configuredKey == Guid.Empty)
                throw new InvalidOperationException("Server-side InstrumentationKey is not configured");

            if (paramPassedKey != configuredKey)
                return Refuse(HttpStatusCode.Forbidden, "That InstrumentationKey is not the one this site imports from.");

            return Ok(new ImportConfig
            {
                Expiry = DateTime.UtcNow.AddMinutes(config.MetadataRefreshMinutes),
                MetadataRefreshMinutes = config.MetadataRefreshMinutes,
            });
        }

        private static readonly Regex Base64 = new Regex("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);

        /// <summary>The InstrumentationKey in a base64-encoded connection string; <see cref="Guid.Empty"/> for anything else.</summary>
        internal static Guid DecodeInstrumentationKey(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return Guid.Empty;

            // The tracker doesn't URL-encode the value, so a '+' in it arrives as a space
            encoded = encoded.Replace(' ', '+');
            if (encoded.Length % 4 != 0 || !Base64.IsMatch(encoded)) return Guid.Empty;

            return ParseInstrumentationKey(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }

        private IHttpActionResult Refuse(HttpStatusCode status, string reason)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(reason, Encoding.UTF8, "text/plain") };
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            return ResponseMessage(response);
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

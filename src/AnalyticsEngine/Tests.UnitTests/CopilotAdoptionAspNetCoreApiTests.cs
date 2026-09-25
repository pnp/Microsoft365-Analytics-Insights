extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using Common.Entities.CopilotAdoption;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Xml.Linq;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionCoordinator = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionAnalysisCoordinator;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using NullAnalysisTelemetry = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.NullCopilotAdoptionAnalysisTelemetry;
using CopilotAdoptionAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotAdoptionAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The two places the Copilot Adoption controller's ASP.NET Core port could drift from the Web API 2
    /// build on <c>dev</c>: how the workbook export reads the reader's per-activity time-saved figures off
    /// the query string, and the JSON of the "still building" 202 the portal polls on.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAspNetCoreApiTests
    {
        [TestMethod]
        public void WorkbookQueryString_FeedsTheTimeSavedParserAsWebApiDid()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var context = new DefaultHttpContext();
                context.Request.QueryString = new QueryString(
                    "?windowDays=28"
                    + "&coworkSendEmailShare=0.15"
                    // Matched case-insensitively, as a bound parameter is.
                    + "&COWORKORGANISEMEETINGSMINUTES=7.5"
                    // A repeated key: Web API's GetQueryNameValuePairs yielded both and the parser kept
                    // the first. ASP.NET Core groups them, and StringValues.ToString() would join them
                    // into an unparseable "0.1,0.9" - so the first value must be what is handed over.
                    + "&coworkCreateDocumentsShare=0.1&coworkCreateDocumentsShare=0.9");

                var parsed = CopilotAdoptionAPIController.ParseTimeSavedOverrides(
                    null, null, null, null, null,
                    CopilotAdoptionAPIController.QueryNameValuePairs(context.Request));

                Assert.AreEqual(0.15d, parsed.CoworkShares[CoworkActivities.SendEmail], "A German server must not read '0.15' as 15.");
                Assert.AreEqual(7.5d, parsed.CoworkMinutes[CoworkActivities.OrganiseMeetings]);
                Assert.AreEqual(0.1d, parsed.CoworkShares[CoworkActivities.CreateDocuments], "The first value of a repeated key wins.");
                Assert.IsFalse(parsed.CoworkShares.ContainsKey(CoworkActivities.PrepareMeetings));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void WorkbookQueryString_NoRequestMeansNoFigures()
        {
            Assert.IsNull(CopilotAdoptionAPIController.QueryNameValuePairs(null));
            Assert.IsFalse(CopilotAdoptionAPIController.ParseTimeSavedOverrides(
                null, null, null, null, null, CopilotAdoptionAPIController.QueryNameValuePairs(new DefaultHttpContext().Request)).Any);
        }

        [TestMethod]
        public void StillBuildingBody_IsWrittenWithTheKeysThePortalPollsOn()
        {
            var settings = WebApiCompatibleJson.Apply(new JsonSerializerSettings());

            var json = JsonConvert.SerializeObject(
                CopilotAdoptionAPIController.StillBuildingBody("00000000000000000000000000000008"), settings);

            StringAssert.Contains(json, "\"status\":\"building\"");
            StringAssert.Contains(json, "\"retryAfterSeconds\":5");
            StringAssert.Contains(json, "\"runId\":\"00000000000000000000000000000008\"");
            StringAssert.Contains(json, "\"message\":");
        }

        /// <summary>
        /// The whole export over HTTP: routing, binding of the named figures, the per-activity figures read
        /// from <c>Request.Query</c> (the port of Web API 2's <c>GetQueryNameValuePairs()</c>), the file
        /// result and the run id header. A figure that did not arrive would not fail anything - the
        /// workbook would simply model the product defaults instead of the hours on the reader's screen -
        /// so this reads the reader's figures back out of the workbook's Settings sheet.
        /// </summary>
        [TestMethod]
        public async Task WorkbookExport_OverHttp_ModelsTheFiguresOnTheQueryString()
        {
            const string runId = "00000000000000000000000000000042";
            var coordinator = new AdoptionCoordinator(
                new FixedAnalysisRunner(MinimalAnalysis(runId)),
                new DictionaryAnalysisCache(),
                (windowDays, hasOverride) => NullAnalysisTelemetry.Instance,
                TimeSpan.FromMinutes(10));

            await using (var app = await StartControllerHost(coordinator))
            using (var client = app.GetTestClient())
            using (var response = await client.GetAsync(
                "/api/CopilotAdoption/export/workbook?windowDays=28"
                + "&copilotMinutesSavedPerMeeting=50"          // a bound parameter
                + "&coworkSendEmailShare=0.5"                   // read from Request.Query by option name
                + "&COWORKSENDEMAILMINUTES=20"                  // ... case-insensitively
                + "&coworkPostInTeamsShare=0.1&coworkPostInTeamsShare=0.9")) // ... first value wins
            {
                var body = await response.Content.ReadAsByteArrayAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, System.Text.Encoding.UTF8.GetString(body));
                Assert.AreEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    response.Content.Headers.ContentType?.MediaType);
                Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
                Assert.IsTrue(response.Headers.TryGetValues(CopilotAdoptionAPIController.RunIdHeader, out var runIds),
                    "The download must name the analysis run it came from.");
                Assert.AreEqual(runId, runIds.Single());

                var settings = SettingsCells(body);
                AssertFollowedBy(settings, "copilotMinutesSavedPerMeeting", "50");
                AssertFollowedBy(settings, "coworkSendEmailShare", "0.5");
                AssertFollowedBy(settings, "coworkSendEmailMinutes", "20");
                AssertFollowedBy(settings, "coworkPostInTeamsShare", "0.1");
            }
        }

        private static CopilotAdoptionAnalysis MinimalAnalysis(string runId)
        {
            var now = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var options = CopilotAdoptionOptions.Default;
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.GeneratedUtc = now;
            analysis.Summary.WindowDays = options.WindowDays;
            analysis.Summary.FromUtc = now.AddDays(-options.WindowDays);
            analysis.Summary.ToUtc = now;
            analysis.Summary.Options = options;
            analysis.Summary.Diagnostics.RunId = runId;
            return analysis;
        }

        private static async Task<WebApplication> StartControllerHost(AdoptionCoordinator coordinator)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services
                .AddControllers()
                .AddWebApiCompatibleJson()
                .ConfigureApplicationPartManager(parts =>
                {
                    parts.ApplicationParts.Clear();
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(CopilotAdoptionAPIController).Assembly));
                    parts.FeatureProviders.Clear();
                    parts.FeatureProviders.Add(new OnlyCopilotAdoptionController());
                })
                .AddControllersAsServices();
            builder.Services.AddScoped(_ => new CopilotAdoptionAPIController(coordinator));
            builder.Services.AddAuthentication(SignedInScheme)
                .AddScheme<AuthenticationSchemeOptions, AlwaysSignedInHandler>(SignedInScheme, null);
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return app;
        }

        private static List<string> SettingsCells(byte[] workbook)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using (var stream = new MemoryStream(workbook))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> names;
                using (var part = zip.GetEntry("xl/workbook.xml").Open())
                {
                    names = XDocument.Load(part).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
                }

                var index = names.IndexOf("Settings");
                Assert.AreNotEqual(-1, index, "The workbook has no Settings sheet.");

                using (var part = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml").Open())
                {
                    return XDocument.Load(part).Descendants(ns + "c")
                        .Select(c => string.Concat(c.Descendants().Where(d => !d.HasElements).Select(d => d.Value)))
                        .ToList();
                }
            }
        }

        private static void AssertFollowedBy(List<string> cells, string key, string expected)
        {
            var index = cells.IndexOf(key);
            Assert.AreNotEqual(-1, index, $"'{key}' is missing from the Settings sheet.");
            Assert.IsTrue(index + 1 < cells.Count, $"'{key}' has no value.");
            Assert.AreEqual(expected, cells[index + 1], $"'{key}' should model the reader's figure.");
        }

        private const string SignedInScheme = "synthetic-signed-in";

        private sealed class OnlyCopilotAdoptionController : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) =>
                typeInfo.AsType() == typeof(CopilotAdoptionAPIController);
        }

        private sealed class AlwaysSignedInHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public AlwaysSignedInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder)
            {
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "admin@contoso.com") }, Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
            }
        }

        private sealed class FixedAnalysisRunner : AdoptionRunner
        {
            private readonly CopilotAdoptionAnalysis _analysis;

            public FixedAnalysisRunner(CopilotAdoptionAnalysis analysis) => _analysis = analysis;

            public Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, List<int> seatLicenceTypeIds, ICopilotAdoptionRunTelemetry telemetry)
                => Task.FromResult(_analysis);
        }

        private sealed class DictionaryAnalysisCache : AdoptionCache
        {
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _entries = new Dictionary<string, CopilotAdoptionAnalysis>();

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                lock (_entries) return _entries.TryGetValue(key, out analysis);
            }

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
                lock (_entries) _entries[key] = analysis;
            }
        }
    }
}

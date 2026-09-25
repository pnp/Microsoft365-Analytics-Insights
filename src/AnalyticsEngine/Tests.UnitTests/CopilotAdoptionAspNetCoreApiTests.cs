extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using Common.Entities.CopilotAdoption;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System.Globalization;
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
    }
}

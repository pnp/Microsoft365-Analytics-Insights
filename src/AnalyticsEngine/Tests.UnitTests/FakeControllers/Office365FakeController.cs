using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Web.Http;

namespace Tests.UnitTests.FakeControllers
{
    public class Office365FakeController : ApiController
    {
        // Process-wide counter so every fake content-summary gets a unique contentId across all pages /
        // time-chunk requests (mirrors real, globally-unique Activity API content blob ids).
        private static int _fakeContentCounter = 0;

        public Office365FakeController()
        {
        }

        [HttpPost]
        [Route("{tenantDomain}/oauth2/token")]
        public HttpResponseMessage GetFakeAuthToken(string tenantDomain)
        {
            // Taken from a real request 30-5-2018.
            string fakeKey = "{\"token_type\":\"Bearer\",\"expires_in\":\"3599\",\"ext_expires_in\":\"0\",\"expires_on\":\"1527703450\",\"not_before\":\"1527699550\",\"resource\":\"https://manage.office.com\",\"access_token\":\"eyFAKETOKEN\"}";
            Console.WriteLine($"--Office365FakeController.GetFakeAuthToken called. Returning '{fakeKey}'");
            return Request.CreateResponse(HttpStatusCode.OK, JObject.Parse(fakeKey));
        }

        #region Subscriptions

        [HttpGet]
        [Route("api/v1.0/{tenantDomain}/activity/feed/subscriptions/list")]
        public HttpResponseMessage ListSubs(Guid tenantDomain)
        {
            // Fake Json data
            string subString = string.Empty;
            using (FakeOfficeServicesDB db = new FakeOfficeServicesDB())
            {
                foreach (var sub in db.subscriptions)
                {
                    subString += "{\"contentType\":\"" + sub.content_type + "\",\"status\":\"enabled\",\"webhook\":null},";
                }
            }
            subString = subString.TrimEnd(",".ToCharArray());
            string allSubsString = $"[{subString}]";

            Console.WriteLine($"--Office365FakeController.ListSubs called. Returning '{subString}'.");

            var allSubs = JsonConvert.DeserializeObject(allSubsString);

            return Request.CreateResponse(HttpStatusCode.OK, allSubs);
        }

        [Route("api/v1.0/{tenantDomain}/activity/feed/subscriptions/start")]
        [HttpPost]
        public HttpResponseMessage StartSubscription(Guid tenantDomain, [FromUri] string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "No contentType");
            }

            using (FakeOfficeServicesDB db = new FakeOfficeServicesDB())
            {
                db.subscriptions.Add(new TestingSubscriptions() { content_type = contentType });
                db.SaveChanges();
            }

            Console.WriteLine($"--Office365FakeController.StartSubscription called. Returning 'HTTP 200'.");

            return Request.CreateResponse(HttpStatusCode.Created);
        }

        #endregion

        [HttpGet]
        [Route("api/v1.0/{tenantDomain}/activity/feed/subscriptions/content")]
        public HttpResponseMessage GetFakeActivitySummary(Guid tenantDomain, [FromUri] bool isNextPage = false)
        {
            string fakeActivity = string.Empty;

            // Shaped like a real request of 30-5-2018, with synthetic identifiers. Each summary gets a UNIQUE contentId/contentUri (via a
            // process-wide counter) - real Activity API content blobs each have a distinct id, and the
            // importer now de-duplicates summaries by contentId (a blob in the time-chunk overlap window is
            // legitimately listed twice), so reusing one id here would collapse every summary into one blob.
            fakeActivity = "[";
            for (int i = 0; i < Constants.ACTIVITIES_PER_SUMMARY; i++)
            {
                var uniqueContentId = "20180530160628319079931$" + System.Threading.Interlocked.Increment(ref _fakeContentCounter) + "$audit_sharepoint$Audit_SharePoint";
                fakeActivity += "{\"contentUri\":\"https://manage.office.com/api/v1.0/00000000-0000-0000-0000-000000000000/activity/feed/audit/" + uniqueContentId + "\",\"contentId\":\"" + uniqueContentId + "\",\"contentType\":\"Audit.SharePoint\",\"contentCreated\":\"2018-05-30T16:06:28.319Z\",\"contentExpiration\":\"2018-06-06T16:06:28.319Z\"}";
                fakeActivity += ",";
            }
            fakeActivity = fakeActivity.TrimEnd(",".ToCharArray());
            fakeActivity += "]";

            var allActivity = JsonConvert.DeserializeObject(fakeActivity);

            HttpResponseMessage r = Request.CreateResponse(HttpStatusCode.OK, allActivity);

            // Every first page gets one fake next page. A next page is told by its query string, not by the tenant in its URL: it
            // used to be the zero tenant id, which is also what a run without secrets is configured with, so there no first page
            // got a next page and FakeActivityTests found half the reports it expected (#860).
            if (!isNextPage)
            {
                r.Headers.Add("NextPageUri", $"https://manage.office.com/api/v1.0/{tenantDomain}/activity/feed/subscriptions/content?isNextPage=true");
            }

            return r;
        }

        [HttpGet]
        [Route("api/v1.0/{tenantDomain}/activity/feed/audit/{contentId}")]
        public HttpResponseMessage GetFakeReportDetails(Guid tenantDomain, string contentId, [FromUri] Guid publisherIdentifier)
        {
            // Shaped like a real request of 30-5-2018, with synthetic identifiers.
            string fakeAuditLogs = "[";
            for (int i = 0; i < Constants.REPORTS_PER_ACTIVITY; i++)
            {
                fakeAuditLogs += "{\"CreationTime\":\"2018-05-30T16:03:20\",\"Id\":\"" + Guid.NewGuid() + "\",\"Operation\":\"PageViewed\",\"OrganizationId\":\"00000000-0000-0000-0000-000000000000\",\"RecordType\":4,\"UserKey\":\"i:0h.f|membership|1003000000000000@live.com\",\"UserType\":0,\"Version\":1,\"Workload\":\"SharePoint\",\"ClientIP\":\"203.0.113.10\",\"ObjectId\":\"https:\\/\\/contoso.sharepoint.com\\/sites\\/SPOInsightsModern\\/_layouts\\/15\\/viewlsts.aspx\",\"UserId\":\"admin@contoso.onmicrosoft.com\",\"CorrelationId\":\"00000000-0000-0000-0000-000000000001\",\"CustomUniqueId\":true,\"EventSource\":\"SharePoint\",\"ItemType\":\"Page\",\"ListItemUniqueId\":\"00000000-0000-0000-0000-000000000002\",\"Site\":\"00000000-0000-0000-0000-000000000003\",\"UserAgent\":\"Mozilla\\/5.0 (Windows NT 10.0; WOW64) AppleWebKit\\/537.36 (KHTML, like Gecko) Chrome\\/66.0.3359.139 Safari\\/537.36\",\"WebId\":\"00000000-0000-0000-0000-000000000004\"}";
                fakeAuditLogs += ",";
            }
            fakeAuditLogs = fakeAuditLogs.TrimEnd(",".ToCharArray());
            fakeAuditLogs += "]";

            Console.WriteLine($"--Office365FakeController.GetFakeAuditLogs called. Returning {Constants.REPORTS_PER_ACTIVITY} reports.");

            var allActivity = JsonConvert.DeserializeObject(fakeAuditLogs);
            return Request.CreateResponse(HttpStatusCode.OK, allActivity);
        }
    }
}

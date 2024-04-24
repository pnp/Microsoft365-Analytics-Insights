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
        public HttpResponseMessage GetFakeActivitySummary(Guid tenantDomain)
        {
            string fakeActivity = string.Empty;
            Guid fakeDomain = default(Guid);

            // Taken from a real request 30-5-2018.
            fakeActivity = "[";
            for (int i = 0; i < Constants.ACTIVITIES_PER_SUMMARY; i++)
            {
                fakeActivity += "{\"contentUri\":\"https://manage.office.com/api/v1.0/34eb3720-3d40-4da8-9f80-f939e220821c/activity/feed/audit/20180530160628319079931$20180530160628319079931$audit_sharepoint$Audit_SharePoint\",\"contentId\":\"20180530160628319079931$20180530160628319079931$audit_sharepoint$Audit_SharePoint\",\"contentType\":\"Audit.SharePoint\",\"contentCreated\":\"2018-05-30T16:06:28.319Z\",\"contentExpiration\":\"2018-06-06T16:06:28.319Z\"}";
                fakeActivity += ",";
            }
            fakeActivity = fakeActivity.TrimEnd(",".ToCharArray());
            fakeActivity += "]";

            var allActivity = JsonConvert.DeserializeObject(fakeActivity);

            HttpResponseMessage r = Request.CreateResponse(HttpStatusCode.OK, allActivity);

            // Add a fake "next page" to not-next-page requests
            if (!fakeDomain.Equals(tenantDomain))
            {
                //Console.WriteLine($"--Office365FakeController.GetFakeActivities called. Returning {Constants.ACTIVITIES_PER_TIME_CHUNK} activities, with next-page results.");
                r.Headers.Add("NextPageUri", $"https://manage.office.com/api/v1.0/{fakeDomain}/activity/feed/subscriptions/content?isNextPage=true");
            }
            else
            {
                // Don't add "next page"
                //Console.WriteLine($"--Office365FakeController.GetFakeActivities called. Returning {Constants.ACTIVITIES_PER_TIME_CHUNK} activities.");
            }

            return r;
        }

        [HttpGet]
        [Route("api/v1.0/{tenantDomain}/activity/feed/audit/{contentId}")]
        public HttpResponseMessage GetFakeReportDetails(Guid tenantDomain, string contentId, [FromUri] Guid publisherIdentifier)
        {
            // Taken from a real request 30-5-2018.
            string fakeAuditLogs = "[";
            for (int i = 0; i < Constants.REPORTS_PER_ACTIVITY; i++)
            {
                fakeAuditLogs += "{\"CreationTime\":\"2018-05-30T16:03:20\",\"Id\":\"" + Guid.NewGuid() + "\",\"Operation\":\"PageViewed\",\"OrganizationId\":\"34eb3720-3d40-4da8-9f80-f939e220821c\",\"RecordType\":4,\"UserKey\":\"i:0h.f|membership|10037ffea94e7503@live.com\",\"UserType\":0,\"Version\":1,\"Workload\":\"SharePoint\",\"ClientIP\":\"83.37.221.242\",\"ObjectId\":\"https:\\/\\/m365x246423.sharepoint.com\\/sites\\/SPOInsightsModern\\/_layouts\\/15\\/viewlsts.aspx\",\"UserId\":\"admin@m365x246423.onmicrosoft.com\",\"CorrelationId\":\"6e3f6c9e-a089-0000-1644-b54c7f86c559\",\"CustomUniqueId\":true,\"EventSource\":\"SharePoint\",\"ItemType\":\"Page\",\"ListItemUniqueId\":\"59a8433d-9bb8-cfef-be22-5dc365e07851\",\"Site\":\"cb8fc1be-12bf-47ff-b45d-397a4a17f10a\",\"UserAgent\":\"Mozilla\\/5.0 (Windows NT 10.0; WOW64) AppleWebKit\\/537.36 (KHTML, like Gecko) Chrome\\/66.0.3359.139 Safari\\/537.36\",\"WebId\":\"30e1714c-fcff-49f9-ade4-f6de4c43d929\"}";
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

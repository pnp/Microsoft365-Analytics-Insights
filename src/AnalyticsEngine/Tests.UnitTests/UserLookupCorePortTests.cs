extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDiagnostics;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.UserDiagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class UserLookupCorePortTests
    {
        [DataTestMethod]
        [DataRow("diagnostics")]
        [DataRow("live-licences")]
        [DataRow("report-verdict")]
        [DataRow("coverage")]
        public async Task DiagnosticRoutes_BindAndReturnTheMissingUpnKey(string route)
        {
            using var host = Host(PortalRoles.Administration, PortalRoles.SeePii);
            var response = await host.Client.GetAsync("api/UserDataLookup/" + route);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("missingUpn", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"]);
        }

        [DataTestMethod]
        [DataRow("diagnostics", false)]
        [DataRow("live-licences", false)]
        [DataRow("report-verdict", false)]
        [DataRow("coverage", false)]
        [DataRow("refresh-status", false)]
        [DataRow("refresh", true)]
        public async Task NewRoutes_RequireBothPermissions(string route, bool post)
        {
            foreach (var role in new[] { PortalRoles.Administration, PortalRoles.SeePii })
            {
                using var host = Host(role);
                using var request = Request(route, post);
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                var response = await host.Client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, route + " with " + role);
            }
        }

        [TestMethod]
        public async Task Refresh_RejectsCrossOriginAndBindsSameOriginJson()
        {
            using var host = Host(PortalRoles.Administration, PortalRoles.SeePii);
            using (var noHeader = Request("refresh", true))
                Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.SendAsync(noHeader)).StatusCode);

            using (var crossOrigin = Request("refresh", true))
            {
                crossOrigin.Headers.Add("X-Requested-With", "XMLHttpRequest");
                crossOrigin.Headers.Add("Sec-Fetch-Site", "cross-site");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client.SendAsync(crossOrigin)).StatusCode);
            }

            using (var sameOrigin = Request("refresh", true))
            {
                sameOrigin.Headers.Add("X-Requested-With", "XMLHttpRequest");
                sameOrigin.Headers.Add("Sec-Fetch-Site", "same-origin");
                var response = await host.Client.SendAsync(sameOrigin);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.AreEqual("missingUpn", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"]);
            }
        }

        [TestMethod]
        public async Task RefreshStatus_WithoutAQueueReturnsBareNotFound()
        {
            using var host = Host(PortalRoles.Administration, PortalRoles.SeePii);
            var response = await host.Client.GetAsync("api/UserDataLookup/refresh-status?id=synthetic-missing");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        }

        private static HttpRequestMessage Request(string route, bool post) =>
            new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "api/UserDataLookup/" + route)
            {
                Content = post ? new StringContent("{\"upn\":\"\"}", Encoding.UTF8, "application/json") : null,
            };

        private static PortalTestHost Host(params string[] roles)
        {
            var unused = new NoExternalServices();
            var diagnostics = new UserDiagnosticsService(unused,
                () => throw new InvalidOperationException("Invalid input must not resolve scope."),
                unused, unused, () => new ImportTaskSettings(), 24);
            return new PortalTestHost(new[] { typeof(UserDataLookupAPIController) },
                PortalTestHost.SignedIn(roles), PortalAccessPolicy.Enforcing,
                _ => new UserDataLookupAPIController(new UserDataLookupService(new SqlUserDataLookupQuery()), () => diagnostics));
        }

        // These requests exercise Core routing, binding and filters, never external services.
        private sealed class NoExternalServices : IUserDiagnosticsQuery, ILiveDirectoryReader, IImportStampReader
        {
            private static Exception Unexpected() => new InvalidOperationException("No SQL, Graph or Storage access was expected.");
            public Task<StoredUserRow> FindUserAsync(string upn) => throw Unexpected();
            public Task<List<StoredLicenceType>> StoredLicenceTypesForUserAsync(int userId) => throw Unexpected();
            public Task<LicenceHistoryRead> HistoryAsync(int userId, int take) => throw Unexpected();
            public Task<LatestStoredRow> LatestAsync(int userId, string categoryKey) => throw Unexpected();
            public Task<List<LicenceTypeRow>> LicenceTypesAsync() => throw Unexpected();
            public Task<DateTime?> LicenceHistoryStartAsync() => throw Unexpected();
            public Task<List<SeatEvidenceRow>> SeatEvidenceAsync(int userId, IReadOnlyList<int> ids, bool history,
                DateTime from, DateTime to, DateTime? start) => throw Unexpected();
            public Task<LiveDirectoryReadResult> ReadAsync(string key, CancellationToken token, string profileSelect = null) => throw Unexpected();
            public Task<IReadOnlyDictionary<string, ImportStamp>> ReadAsync(IEnumerable<string> keys) => throw Unexpected();
        }
    }
}

extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.LicenceActivity;
using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Dispatcher;

namespace Tests.UnitTests
{
    [TestClass]
    public class LicenceActivityApiTests
    {
        [TestMethod]
        public async Task AnonymousRequestsAreDenied_AggregatesDoNotRequireTheDetailRole()
        {
            using (var app = new Harness())
            {
                app.Principal = new GenericPrincipal(new GenericIdentity(string.Empty), new string[0]);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("api/LicenceActivity/availability")).StatusCode);
                app.Principal = SignedIn();
                var availability = await app.Json("api/LicenceActivity/availability");
                Assert.AreEqual(false, (bool)availability["canViewUsers"]);
                Assert.AreEqual(true, (bool)availability["available"]);
                var overview = await app.Json("api/LicenceActivity/overview");
                Assert.IsNotNull(overview["snapshotId"]);
                Assert.AreEqual(HttpStatusCode.Forbidden,
                    (await app.Client.GetAsync("api/LicenceActivity/users?overviewId=" + overview["snapshotId"] + "&licenceTypeId=1")).StatusCode);
                Assert.AreEqual(0, app.Store.UserCalls);
            }
        }

        [TestMethod]
        public async Task DisabledPrerequisite_IsExplicitAndDoesNotQueryStorage()
        {
            using (var app = new Harness())
            {
                app.Sources.UserMetadata = false;
                var availability = await app.Json("api/LicenceActivity/availability");
                Assert.AreEqual(false, (bool)availability["available"]);
                StringAssert.Contains(availability["messages"].ToString(), "GraphUsersMetadata");
                Assert.AreEqual(HttpStatusCode.PreconditionFailed, (await app.Client.GetAsync("api/LicenceActivity/overview")).StatusCode);
                Assert.AreEqual(0, app.Store.OverviewCalls);
            }
        }

        [TestMethod]
        public async Task ConcurrentSameCookieRequests_ShareOneColdQueryAndReturnWithoutPolling()
        {
            using (var app = new Harness())
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                app.Store.OverviewLoader = async () =>
                {
                    entered.TrySetResult(true);
                    await release.Task;
                    return LicenceActivityTests.SampleOverview();
                };
                var first = app.Client.GetAsync("api/LicenceActivity/overview");
                await entered.Task;
                var second = app.Client.GetAsync("api/LicenceActivity/overview");
                var third = app.Client.GetAsync("api/LicenceActivity/overview");
                release.TrySetResult(true);
                foreach (var response in await Task.WhenAll(first, second, third))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.IsTrue(response.Headers.CacheControl.NoStore);
                }
                Assert.AreEqual(1, app.Store.OverviewCalls);
            }
        }

        [TestMethod]
        public async Task InvalidQueriesFailBeforeSql_CustomScopeIsInheritedByUsers()
        {
            using (var app = new Harness())
            {
                Assert.AreEqual(HttpStatusCode.BadRequest,
                    (await app.Client.GetAsync("api/LicenceActivity/overview?from=2000-06-29&to=2000-06-30")).StatusCode);
                Assert.AreEqual(HttpStatusCode.BadRequest,
                    (await app.Client.GetAsync("api/LicenceActivity/overview?departmentId=not-an-integer")).StatusCode);
                Assert.AreEqual(0, app.Store.OverviewCalls);
                app.Principal = SignedIn(LicenceActivityAPIController.UserDetailRole);
                var overview = await app.Json("api/LicenceActivity/overview?from=2000-05-02&to=2000-06-22&departmentId=7&countryId=0");
                var id = (string)overview["snapshotId"];
                var users = await app.Json("api/LicenceActivity/users?overviewId=" + id + "&licenceTypeId=1&workload=outlook&top=25&search=Contoso&page=2&pageSize=10&sort=activity&direction=desc");
                Assert.AreEqual("2000-05-02", (string)users["query"]["from"]);
                Assert.AreEqual("2000-06-22", (string)users["query"]["to"]);
                Assert.AreEqual(7, (int)users["query"]["departmentId"]);
                Assert.AreEqual(0, (int)users["query"]["countryId"]);
                Assert.AreEqual(25, (int)users["query"]["top"]);
                Assert.AreEqual("Contoso", (string)users["query"]["search"]);
                Assert.AreEqual(HttpStatusCode.BadRequest,
                    (await app.Client.GetAsync("api/LicenceActivity/users?overviewId=" + id + "&licenceTypeId=1&pageSize=101")).StatusCode);
                Assert.AreEqual(HttpStatusCode.NotFound,
                    (await app.Client.GetAsync("api/LicenceActivity/users?overviewId=" + id + "&licenceTypeId=999")).StatusCode);
                Assert.AreEqual(1, app.Store.UserCalls);
            }
        }

        [TestMethod]
        public async Task Excel_IsTheCachedCurrentSnapshot_RechecksDetailAccess_AndRefusesExpiry()
        {
            using (var app = new Harness())
            {
                app.Principal = SignedIn(LicenceActivityAPIController.UserDetailRole);
                var overview = await app.Json("api/LicenceActivity/overview");
                var id = (string)overview["snapshotId"];
                var users = await app.Json("api/LicenceActivity/users?overviewId=" + id + "&licenceTypeId=1");
                var exportUrl = "api/LicenceActivity/export?overviewId=" + id + "&usersId=" + users["snapshotId"];
                var export = await app.Client.GetAsync(exportUrl);
                Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
                Assert.AreEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType.MediaType);
                Assert.IsTrue(export.Content.Headers.ContentDisposition.FileName.EndsWith(".xlsx"));
                using (var zip = new ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync())))
                    Assert.IsNotNull(zip.GetEntry("xl/workbook.xml"));
                Assert.AreEqual(1, app.Store.OverviewCalls, "Export must not run a fresh overview query.");
                Assert.AreEqual(1, app.Store.UserCalls, "Export must not run a fresh user query.");
                app.Principal = SignedIn();
                Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client.GetAsync(exportUrl)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + id)).StatusCode);
                app.Now = app.Now.AddMinutes(6);
                Assert.AreEqual(HttpStatusCode.Gone, (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + id)).StatusCode);
                Assert.AreEqual(1, app.Store.OverviewCalls);
            }
        }

        [TestMethod]
        public async Task ExpiredUserSnapshot_CanReloadUnderTheSameOverview_AndMismatchedExportIsRejected()
        {
            using (var app = new Harness())
            {
                app.Principal = SignedIn(LicenceActivityAPIController.UserDetailRole);
                var overview = await app.Json("api/LicenceActivity/overview");
                var overviewId = (string)overview["snapshotId"];
                var usersUrl = "api/LicenceActivity/users?overviewId=" + overviewId + "&licenceTypeId=1";
                var firstUsers = await app.Json(usersUrl);
                var firstUsersId = (string)firstUsers["snapshotId"];

                // Users expire sooner than their overview. Refreshing the overview alone returns
                // the same snapshot, so the browser must explicitly reload the current user query.
                app.Now = app.Now.AddMinutes(2).AddSeconds(1);
                var sameOverview = await app.Json("api/LicenceActivity/overview");
                Assert.AreEqual(overviewId, (string)sameOverview["snapshotId"]);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + overviewId)).StatusCode);
                Assert.AreEqual(HttpStatusCode.Gone,
                    (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + overviewId + "&usersId=" + firstUsersId)).StatusCode);

                var freshUsers = await app.Json(usersUrl);
                var freshUsersId = (string)freshUsers["snapshotId"];
                Assert.AreNotEqual(firstUsersId, freshUsersId);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + overviewId + "&usersId=" + freshUsersId)).StatusCode);
                Assert.AreEqual(1, app.Store.OverviewCalls, "Reloading an expired user page must not require a different overview.");
                Assert.AreEqual(2, app.Store.UserCalls);

                var otherOverview = await app.Json("api/LicenceActivity/overview?departmentId=7");
                Assert.AreEqual(HttpStatusCode.Conflict,
                    (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + otherOverview["snapshotId"] + "&usersId=" + freshUsersId)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK,
                    (await app.Client.GetAsync("api/LicenceActivity/export?overviewId=" + overviewId + "&usersId=" + freshUsersId)).StatusCode);
                Assert.AreEqual(2, app.Store.OverviewCalls);
                Assert.AreEqual(2, app.Store.UserCalls, "Neither a refused export nor a valid export may query storage.");
            }
        }

        [TestMethod]
        public async Task FailedRun_IsTerminalAndRetryable_NotAnEmptySuccessfulReport()
        {
            using (var app = new Harness())
            {
                app.Store.OverviewLoader = () => Task.FromException<LicenceActivityOverview>(new InvalidOperationException("private filter"));
                var failed = await app.Client.GetAsync("api/LicenceActivity/overview");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(5), failed.Headers.RetryAfter.Delta);
                Assert.IsFalse((await failed.Content.ReadAsStringAsync()).Contains("private filter"));
                app.Store.OverviewLoader = () => Task.FromResult(LicenceActivityTests.SampleOverview());
                Assert.AreEqual(HttpStatusCode.OK, (await app.Client.GetAsync("api/LicenceActivity/overview")).StatusCode);
                Assert.AreEqual(2, app.Store.OverviewCalls);
            }
        }

        private static IPrincipal SignedIn(params string[] roles)
        {
            var identity = new ClaimsIdentity("synthetic-test");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-reader"));
            foreach (var role in roles) identity.AddClaim(new Claim(ClaimTypes.Role, role));
            return new ClaimsPrincipal(identity);
        }

        private sealed class Harness : IDisposable
        {
            private readonly LicenceActivityHttpHost _host;
            internal DateTime Now = LicenceActivityTests.Now;
            internal IPrincipal Principal = SignedIn();
            internal readonly FakeStore Store = new FakeStore();
            internal readonly LicenceActivitySources Sources = new LicenceActivitySources
            {
                UserMetadata = true, UsageReports = true, NowUtc = LicenceActivityTests.Now
            };
            internal HttpClient Client => _host.Client;

            internal Harness()
            {
                _host = new LicenceActivityHttpHost(Store, Sources, () => Now, () => Principal);
            }

            internal async Task<JObject> Json(string url)
            {
                var response = await Client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
                return JObject.Parse(body);
            }
            public void Dispose() => _host.Dispose();
        }

        private sealed class FakeStore : ILicenceActivityStore
        {
            internal int OverviewCalls;
            internal int UserCalls;
            internal Func<Task<LicenceActivityOverview>> OverviewLoader = () => Task.FromResult(LicenceActivityTests.SampleOverview());
            public Task<LicenceActivityOverview> LoadOverviewAsync(LicenceActivityQuery query, LicenceActivitySources sources,
                ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref OverviewCalls);
                return OverviewLoader();
            }
            public Task<LicenceActivityUsers> LoadUsersAsync(LicenceActivityOverview overview, LicenceActivityQuery query,
                LicenceActivitySources sources, ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref UserCalls);
                return Task.FromResult(new LicenceActivityUsers { TotalUsers = 3, RankedUsers = 2 });
            }
        }

    }
}

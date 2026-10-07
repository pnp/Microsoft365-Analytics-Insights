extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.ActivityAnalysis;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.ActivityAnalysis;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Activity analysis API over HTTP, in memory: the JSON contract the portal is built against, the refusals and
    /// their codes, and what a reader with and without See PII is sent. All data is synthetic.
    /// </summary>
    [TestClass]
    public class ActivityAnalysisApiTests
    {
        private const string Period = "from=2026-01-05&to=2026-02-02";
        private const string Report = "api/ActivityAnalysis/report?" + Period + "&metrics=teams.calls,teams.meetings";

        [TestMethod]
        public async Task Availability_IsForEveryReader_InTheContractsShape()
        {
            using (var app = new Harness())
            {
                var response = await app.Host.Client.GetAsync("api/ActivityAnalysis/availability");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl.NoStore);
                Assert.AreEqual("application/json", response.Content.Headers.ContentType.MediaType);

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(true, (bool)json["available"]);
                Assert.AreEqual(JTokenType.Null, json["reason"].Type);
                Assert.AreEqual("2025-12-29", (string)json["earliestWeek"]);
                Assert.AreEqual("2026-02-02", (string)json["latestWeek"]);
                Assert.AreEqual("2025-12-29", (string)json["defaultFrom"], "Fewer than 52 weeks compiled: from the first.");
                Assert.AreEqual("2026-02-02", (string)json["defaultTo"]);
                Assert.AreEqual(105, (int)json["maximumWeeks"]);
                Assert.AreEqual(6, json["categories"].Count());
                Assert.AreEqual(58, json["metrics"].Count());
                Assert.AreEqual(0, app.Source.ReadModelLoads, "Availability never reads the weekly table's rows.");
            }
        }

        [TestMethod]
        public async Task NotInstalled_IsAReasonOnAvailability_AndA412Elsewhere()
        {
            using (var app = new Harness())
            {
                app.Source.Schema = ActivityAnalysisSchema.NotInstalled;
                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);

                var availability = await app.Json("api/ActivityAnalysis/availability");
                Assert.AreEqual(false, (bool)availability["available"]);
                Assert.AreEqual("notInstalled", (string)availability["reason"]);

                foreach (var url in new[] { Report, "api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls" })
                {
                    var response = await app.Host.Client.GetAsync(url);
                    Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode, url);
                    Assert.AreEqual("notInstalled", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"]);
                }

                Assert.AreEqual(0, app.Source.ReadModelLoads);
            }
        }

        [DataTestMethod]
        [DataRow("metrics=teams.nope", "invalidMetric")]
        [DataRow("", "invalidMetric")]
        [DataRow("metrics=teams.calls&ranges=teams.calls:9:1", "invalidRange")]
        [DataRow("metrics=teams.calls&ranges=teams.calls", "invalidRange")]
        [DataRow("metrics=teams.calls&from=2026-02-02&to=2026-01-05", "invalidPeriod")]
        [DataRow("metrics=teams.calls&from=2020-01-06&to=2026-01-05", "invalidPeriod")]
        [DataRow("metrics=teams.calls&from=yesterday", "invalidPeriod")]
        [DataRow("metrics=teams.calls&userFilter=not-json", "invalidFilter")]
        [DataRow("metrics=teams.calls&licences=E3", "invalidFilter")]
        public async Task Report_RefusesAMalformedRequest_WithItsStableCode_BeforeReadingAnyRows(string query, string code)
        {
            using (var app = new Harness())
            {
                var response = await app.Host.Client.GetAsync("api/ActivityAnalysis/report?" + query);

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(code, (string)json["code"]);
                Assert.IsFalse(string.IsNullOrWhiteSpace((string)json["message"]), "English for anyone who is not the portal.");
                Assert.AreEqual(0, app.Source.ReadModelLoads);
            }
        }

        [TestMethod]
        public async Task Report_IsInTheContractsShape()
        {
            using (var app = new Harness())
            {
                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var response = await app.Host.Client.GetAsync(Report);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl.NoStore);

                var body = await response.Content.ReadAsStringAsync();
                StringAssert.Matches(body, new Regex("\"generatedUtc\":\"\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ\""));
                var json = JObject.Parse(body);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "generatedUtc", "from", "to", "weekStarts", "metrics", "populationPeople", "matchingPeople", "activePeople",
                        "suppressed", "series", "byCompany", "byDepartment", "departments", "otherDepartments", "total", "licences",
                        "rangeMaxima", "userFilter",
                    },
                    json.Properties().Select(p => p.Name).ToList());

                Assert.AreEqual(5, json["weekStarts"].Count());
                CollectionAssert.AreEqual(new[] { "teams.calls", "teams.meetings" }, json["metrics"].Select(m => (string)m).ToList());
                Assert.AreEqual(10, (int)json["populationPeople"]);
                Assert.AreEqual(JTokenType.Null, json["userFilter"].Type);
                CollectionAssert.AreEqual(new[] { "metric", "sum", "activePeople" }, ((JObject)json["series"][0]).Properties().Select(p => p.Name).ToList());
                Assert.AreEqual(5, json["series"][0]["sum"].Count());
                CollectionAssert.AreEqual(new[] { "rows", "otherGroups" }, ((JObject)json["byCompany"]).Properties().Select(p => p.Name).ToList());
                CollectionAssert.AreEqual(new[] { "name", "other", "activePeople" }, ((JObject)json["byCompany"]["rows"][0]).Properties().Select(p => p.Name).ToList());
                CollectionAssert.AreEqual(new[] { "name", "other", "people", "values" }, ((JObject)json["departments"][0]).Properties().Select(p => p.Name).ToList());
                CollectionAssert.AreEqual(new[] { "metric", "sum", "unique" }, ((JObject)json["total"]["values"][0]).Properties().Select(p => p.Name).ToList());
                CollectionAssert.AreEqual(new[] { "id", "name", "skuId", "people" }, ((JObject)json["licences"][0]).Properties().Select(p => p.Name).ToList());
                CollectionAssert.AreEqual(new[] { "metric", "max" }, ((JObject)json["rangeMaxima"][0]).Properties().Select(p => p.Name).ToList());
            }
        }

        [TestMethod]
        public async Task AReaderWithoutSeePii_GetsSmallGroupsFolded_AndAHandfulOfPeopleSuppressed()
        {
            using (var app = new Harness())
            {
                var folded = await app.Json(Report);
                Assert.AreEqual(false, (bool)folded["suppressed"]);
                CollectionAssert.AreEqual(new[] { "Sales", null }, folded["departments"].Select(d => (string)d["name"]).ToList());
                Assert.AreEqual(true, (bool)folded["departments"][1]["other"]);
                Assert.AreEqual(3, (int)folded["otherDepartments"]);

                // No filter of the reader's own: the last two weeks of the period are only three people's, so the report
                // of everyone in them is a handful of people's records.
                const string lastTwoWeeks = "api/ActivityAnalysis/report?from=2026-01-26&to=2026-02-02&metrics=teams.calls,teams.meetings";
                var suppressed = await app.Json(lastTwoWeeks);
                Assert.AreEqual(true, (bool)suppressed["suppressed"]);
                Assert.AreEqual(3, (int)suppressed["matchingPeople"]);
                Assert.AreEqual(3, (int)suppressed["populationPeople"]);
                Assert.AreEqual(0, (int)suppressed["activePeople"]);
                Assert.AreEqual(0, suppressed["series"].Count());
                Assert.AreEqual(0, suppressed["departments"].Count());
                Assert.AreEqual(0, suppressed["total"]["values"].Count());
                Assert.AreEqual(0, suppressed["rangeMaxima"].Count(), "Each bound would be one of the three's own total.");
                Assert.AreEqual(0, app.Source.WeeklyTotalsLoads, "Nothing is read for figures that are not shown.");

                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var named = await app.Json(lastTwoWeeks);
                Assert.AreEqual(false, (bool)named["suppressed"]);
                Assert.AreEqual(3, (int)named["matchingPeople"]);
                CollectionAssert.AreEqual(new[] { "Sales", null }, named["departments"].Select(d => (string)d["name"]).ToList(),
                    "Two in Sales, and one missing from the directory - each shown to a reader with See PII.");
                Assert.AreEqual(false, (bool)named["departments"][1]["other"]);
            }
        }

        /// <summary>
        /// Any condition the reader chooses can be differenced against the report without it: "every licence but X", or
        /// a range one below the top total, leaves out exactly one person, whose figures are then the difference.
        /// </summary>
        [DataTestMethod]
        [DataRow("&licences=8", 2)]
        [DataRow("&licences=7,9", 9)]
        [DataRow("&ranges=teams.calls:5:", 4)]
        [DataRow("&ranges=teams.calls::8", 9)]
        [DataRow("&ranges=teams.calls:0:", 10)]
        [DataRow("&licences=7&ranges=teams.calls:1:", 6)]
        public async Task LicenceAndActivityFilters_NeedSeePii(string conditions, int matchingWithSeePii)
        {
            using (var app = new Harness())
            {
                var refused = await app.Host.Client.GetAsync(Report + conditions);
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, conditions);
                var refusal = JObject.Parse(await refused.Content.ReadAsStringAsync());
                Assert.AreEqual("portalPermissionRequired", (string)refusal["code"]);
                Assert.AreEqual("seePii", (string)refusal["permission"]);
                Assert.AreEqual(0, app.Source.ReadModelLoads, "A refused request reads nothing.");

                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var named = await app.Json(Report + conditions);
                Assert.AreEqual(10, (int)named["populationPeople"]);
                Assert.AreEqual(matchingWithSeePii, (int)named["matchingPeople"], conditions);
            }
        }

        [TestMethod]
        public async Task AReaderWithoutSeePii_MayStillSendConditionsThatNarrowNothing()
        {
            using (var app = new Harness())
            {
                // A range with neither bound is no condition at all.
                var report = await app.Json(Report + "&ranges=teams.calls::");
                Assert.AreEqual(10, (int)report["matchingPeople"]);
                Assert.AreEqual(false, (bool)report["suppressed"]);
            }
        }

        [TestMethod]
        public async Task RangeMaxima_AreSeePiis_DecidedPerRequest_OverTheSharedReadModel()
        {
            using (var app = new Harness())
            {
                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var named = await app.Json(Report);
                Assert.AreEqual(ActivityAnalysisMetricCatalogue.Count, named["rangeMaxima"].Count());
                Assert.AreEqual(9, (long)named["rangeMaxima"].Single(m => (string)m["metric"] == "teams.calls")["max"],
                    "user10's nine calls: one person's total.");

                // The same period from the same caches, straight after: nothing the first reader was sent is reused.
                app.Host.Principal = PortalTestHost.SignedIn();
                var anonymous = await app.Json(Report);
                Assert.AreEqual(JTokenType.Array, anonymous["rangeMaxima"].Type);
                Assert.AreEqual(0, anonymous["rangeMaxima"].Count());
                Assert.AreEqual(10, (int)anonymous["matchingPeople"], "The report of everyone is still every reader's.");

                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                Assert.AreEqual(ActivityAnalysisMetricCatalogue.Count, (await app.Json(Report))["rangeMaxima"].Count());
                Assert.AreEqual(1, app.Source.ReadModelLoads, "One read model, shared by both readers.");
            }
        }

        [TestMethod]
        public async Task ThePeopleFilter_NeedsSeePii()
        {
            using (var app = new Harness())
            {
                var marketing = Uri.EscapeDataString("[{\"d\":\"department\",\"v\":[\"Marketing\"]}]");

                // Five named people, then each set of four, would give each one's figures: refused outright.
                var refused = await app.Host.Client.GetAsync(Report + "&userFilter=" + marketing);
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
                var refusal = JObject.Parse(await refused.Content.ReadAsStringAsync());
                Assert.AreEqual("portalPermissionRequired", (string)refusal["code"]);
                Assert.AreEqual("seePii", (string)refusal["permission"]);
                Assert.AreEqual(0, app.Source.ReadModelLoads, "A refused request reads nothing.");

                var unfiltered = await app.Json(Report + "&userFilter=" + Uri.EscapeDataString("[]"));
                Assert.AreEqual(10, (int)unfiltered["matchingPeople"], "An empty filter narrows nothing, so it is not refused.");

                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var named = await app.Json(Report + "&userFilter=" + marketing);
                Assert.AreEqual(2, (int)named["matchingPeople"]);
                CollectionAssert.AreEqual(new[] { "Marketing" }, named["departments"].Select(d => (string)d["name"]).ToList());
                Assert.AreEqual("department", (string)named["userFilter"]["clauses"][0]["dimension"], "The filter is echoed as applied.");
            }
        }

        [TestMethod]
        public async Task People_NeedSeePii_AndComeLargestFirst()
        {
            using (var app = new Harness())
            {
                var refused = await app.Host.Client.GetAsync("api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls");
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
                var refusal = JObject.Parse(await refused.Content.ReadAsStringAsync());
                Assert.AreEqual("portalPermissionRequired", (string)refusal["code"]);
                Assert.AreEqual("seePii", (string)refusal["permission"]);
                Assert.AreEqual(0, app.Source.ReadModelLoads, "A refused request reads nothing.");

                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var sales = await app.Json("api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls,teams.meetings&department=Sales&top=2");
                CollectionAssert.AreEqual(new[] { "department", "noDepartment", "totalPeople", "truncated", "people" }, sales.Properties().Select(p => p.Name).ToList());
                Assert.AreEqual("Sales", (string)sales["department"]);
                Assert.AreEqual(5, (int)sales["totalPeople"]);
                Assert.AreEqual(true, (bool)sales["truncated"]);
                CollectionAssert.AreEqual(new[] { "user4@contoso.com", "user1@contoso.com" }, sales["people"].Select(p => (string)p["userPrincipalName"]).ToList());
                CollectionAssert.AreEqual(new[] { "userPrincipalName", "department", "values" }, ((JObject)sales["people"][0]).Properties().Select(p => p.Name).ToList());
                Assert.AreEqual(7, (long)sales["people"][0]["values"][0]["sum"]);

                var byMeetings = await app.Json("api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls,teams.meetings&sort=teams.meetings&top=1");
                Assert.AreEqual("user7@contoso.com", (string)byMeetings["people"][0]["userPrincipalName"], "The champions list: everyone, by the chosen metric.");

                var notSet = await app.Json("api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls&noDepartment=true");
                Assert.AreEqual(2, (int)notSet["totalPeople"]);
                Assert.AreEqual(true, (bool)notSet["noDepartment"]);

                foreach (var bad in new[] { "&sort=teams.meetings", "&department=Sales&noDepartment=true", "&top=lots" })
                {
                    var response = await app.Host.Client.GetAsync("api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls" + bad);
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, bad);
                }
            }
        }

        [TestMethod]
        public async Task TheGlobalFilter_IsThePopulation()
        {
            using (var app = new Harness(globalFilter: "[{\"d\":\"department\",\"v\":[\"Sales\"]}]"))
            {
                app.Host.Principal = PortalTestHost.SignedIn(PortalRoles.SeePii);
                var report = await app.Json(Report);

                Assert.AreEqual(5, (int)report["populationPeople"]);
                Assert.AreEqual(5, (int)report["matchingPeople"]);
                CollectionAssert.AreEqual(new[] { "Sales" }, report["departments"].Select(d => (string)d["name"]).ToList());

                var everyone = await app.Json("api/ActivityAnalysis/people?" + Period + "&metrics=teams.calls&top=500");
                Assert.AreEqual(5, (int)everyone["totalPeople"], "Nobody outside the administrator's filter is listed.");
            }
        }

        [TestMethod]
        public async Task AnotherPeriodLoading_TooLong_IsA503WithRetryAfter()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var app = new Harness(caches: new ActivityAnalysisCaches(loadWait: TimeSpan.FromMilliseconds(200))))
            {
                app.Source.BeforeReadModel = async () =>
                {
                    started.TrySetResult(true);
                    await release.Task;
                };

                var first = app.Host.Client.GetAsync(Report);
                await started.Task.WithTimeout(TimeSpan.FromSeconds(30));

                var busy = await app.Host.Client.GetAsync("api/ActivityAnalysis/report?from=2026-01-05&to=2026-01-26&metrics=teams.calls");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.AreEqual("activityAnalysisBusy", (string)JObject.Parse(await busy.Content.ReadAsStringAsync())["code"]);
                Assert.AreEqual(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter.Delta);

                release.SetResult(true);
                Assert.AreEqual(HttpStatusCode.OK, (await first.WithTimeout(TimeSpan.FromSeconds(30))).StatusCode);
            }
        }

        [TestMethod]
        public async Task Loads_AreTimedInTelemetry_WithFactsOnly_AndTelemetryNeverFailsALoad()
        {
            var events = new List<KeyValuePair<Dictionary<string, string>, Dictionary<string, double>>>();
            var source = ActivityAnalysisReadModelTests.Source();
            var telemetry = new ActivityAnalysisTelemetrySource(source, (d, m) => events.Add(new KeyValuePair<Dictionary<string, string>, Dictionary<string, double>>(d, m)));
            var period = ActivityAnalysisPeriod.Create(new DateTime(2026, 1, 5), new DateTime(2026, 2, 2));

            var model = await telemetry.LoadReadModelAsync(period, CancellationToken.None);
            await telemetry.LoadWeeklyTotalsAsync(model, new[] { 1, 2 }, CancellationToken.None);
            source.Schema = ActivityAnalysisSchema.NotInstalled;
            await Assert.ThrowsExceptionAsync<ActivityAnalysisNotInstalledException>(() => telemetry.LoadReadModelAsync(period, CancellationToken.None));

            Assert.AreEqual(3, events.Count);
            Assert.AreEqual("ReadModel", events[0].Key["Stage"]);
            Assert.AreEqual("Completed", events[0].Key["Outcome"]);
            Assert.AreEqual(10, events[0].Value["People"]);
            Assert.AreEqual(5, events[0].Value["Weeks"]);
            Assert.AreEqual(model.ApproximateBytes, events[0].Value["ApproximateBytes"]);
            Assert.IsTrue(events[0].Value.ContainsKey("DurationMs"));
            Assert.AreEqual("WeeklyTotals", events[1].Key["Stage"]);
            Assert.AreEqual(2, events[1].Value["People"]);
            Assert.AreEqual("Failed", events[2].Key["Outcome"]);
            Assert.AreEqual(nameof(ActivityAnalysisNotInstalledException), events[2].Key["ExceptionType"]);
            Assert.IsFalse(events.SelectMany(e => e.Key.Values).Any(v => v.Contains("@") || v.Contains("Sales")), "Facts only - never a name.");

            source.Schema = null;
            var broken = new ActivityAnalysisTelemetrySource(source, (d, m) => throw new InvalidOperationException("Synthetic telemetry outage."));
            Assert.AreEqual(10, (await broken.LoadReadModelAsync(period, CancellationToken.None)).PeopleCount);
        }

        [TestMethod]
        public void TheHost_LetsThroughTheLongestQueryThePortalBuilds()
        {
            // A request line over the host's limit is refused before the controller can explain anything - and every
            // metric selected comes close to IIS's 2,048-character query-string default on its own.
            // net10: the host is Kestrel, whose request line defaults to 8 KB. Program.cs raises it to 16 KB for every
            // endpoint, as it already did for Copilot Adoption's filters, so there is no Web.Template.config <location>
            // to raise it in: stable build 1850's api/ActivityAnalysis allowance is deliberately not carried over.
            var directory = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string program = null;
            while (directory != null && program == null)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, "Web", "Program.cs");
                program = System.IO.File.Exists(candidate) ? System.IO.File.ReadAllText(candidate) : null;
                directory = directory.Parent;
            }

            Assert.IsNotNull(program, "Web/Program.cs was not found.");
            StringAssert.Contains(program, "options.Limits.MaxRequestLineSize = 16 * 1024");

            var keys = ActivityAnalysisMetricCatalogue.All.Select(m => m.Key).ToList();
            var query = Period
                + "&metrics=" + Uri.EscapeDataString(string.Join(",", keys))
                + "&ranges=" + Uri.EscapeDataString(string.Join(",", keys.Select(k => k + ":1000000:9999999")))
                + "&licences=" + Uri.EscapeDataString(string.Join(",", Enumerable.Range(1000, 100)))
                + "&userFilter=" + new string('x', 6000);
            Console.WriteLine("Longest realistic query string: {0:N0} characters.", query.Length);
            Assert.IsTrue(query.Length < 12000, "The figure main's Web.Template.config quotes.");

            var requestLine = "GET /api/ActivityAnalysis/report?" + query + " HTTP/1.1";
            Assert.IsTrue(requestLine.Length < 16 * 1024, "The longest request line must fit Kestrel's 16 KB limit.");
        }

        private sealed class Harness : IDisposable
        {
            internal Harness(string globalFilter = null, ActivityAnalysisCaches caches = null)
            {
                Source = ActivityAnalysisReadModelTests.Source();
                var service = new ActivityAnalysisService(Source, "synthetic-api", caches ?? new ActivityAnalysisCaches());
                var filters = globalFilter == null
                    ? GlobalFilterProviders.None
                    : GlobalFilterProviders.Fixed(new GlobalFilterRecord { StorageAvailable = true, FilterJson = globalFilter, Revision = 1 });
                var scopes = new ReportScopeResolver(filters, new FixedDirectory(ActivityAnalysisReadModelTests.Directory()));
                Host = new PortalTestHost(
                    new[] { typeof(ActivityAnalysisAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new ActivityAnalysisAPIController(() => service, scopes, () => new DateTime(2026, 2, 9, 9, 0, 0, DateTimeKind.Utc)));
            }

            internal ActivityAnalysisFakeSource Source { get; }

            internal PortalTestHost Host { get; }

            internal async Task<JObject> Json(string url)
            {
                var response = await Host.Client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url + ": " + body);
                return JObject.Parse(body);
            }

            public void Dispose() => Host.Dispose();
        }

        private sealed class FixedDirectory : IUserDirectorySource
        {
            private readonly UserDirectorySnapshot _snapshot;

            internal FixedDirectory(UserDirectorySnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

            public void Prefetch()
            {
            }

            public void Invalidate()
            {
            }
        }
    }
}

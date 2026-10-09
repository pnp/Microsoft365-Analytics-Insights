extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Agent365;
using Common.Entities.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using CatalogController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.Agent365PackageCatalogAPIController;

namespace Tests.UnitTests
{
    [TestClass]
    public class Agent365PackageCatalogSqlTests
    {
        [TestMethod]
        public async Task SqlDatetime2_CatalogAndHealthReadsRestoreUtcKindAndSerializeWithZ()
        {
            using (var scratch = ScratchDatabase.Create("Agent365Utc"))
            {
                scratch.Execute(Agent365PackageCatalog.Up_Sql);
                var instant = new DateTime(2026, 10, 1, 0, 15, 0, DateTimeKind.Utc);
                var store = new Agent365PackageCatalogStore(new ScratchContextFactory(scratch.ConnectionString));
                var successfulRun = await store.BeginImportAsync(instant);
                await store.SavePageAsync(successfulRun, new[]
                {
                    new Agent365Package
                    {
                        PackageId = "contoso-used", DisplayName = "Contoso used agent",
                        LastModifiedUtc = instant, LastUsedUtc = instant, LastUsedDateTimeProvided = true,
                    },
                    new Agent365Package
                    {
                        PackageId = "contoso-never", DisplayName = "Contoso never-used agent",
                        LastUsedDateTimeProvided = true,
                    },
                    new Agent365Package { PackageId = "contoso-unknown", DisplayName = "Contoso unknown usage" },
                });
                await store.CompleteImportAsync(successfulRun, instant.AddMinutes(1), 3, 0);
                var failedRun = await store.BeginImportAsync(instant.AddHours(1));
                await store.FailImportAsync(failedRun, instant.AddHours(1).AddMinutes(1), "Synthetic import failure");

                var raw = (DateTime)scratch.Scalar("SELECT TOP (1) last_modified_utc FROM dbo.copilot_agent_packages WHERE package_id = N'contoso-used';");
                Assert.AreEqual(DateTimeKind.Unspecified, raw.Kind, "The fixture must cross the actual datetime2 encoding boundary.");
                var health = await store.GetImportHealthAsync();
                AssertUtc(instant.AddHours(1), health.LastAttemptUtc);
                AssertUtc(instant.AddHours(1).AddMinutes(1), health.LastAttemptCompletedUtc);
                AssertUtc(instant.AddMinutes(1), health.LastSuccessfulImportUtc);
                Assert.IsFalse(health.LastAttemptSucceeded.Value);
                Assert.AreEqual(1, health.NeverUsedCount);

                var page = await store.GetCurrentPageAsync(0, 50, false);
                var used = page.Packages.Single(p => p.PackageId == "contoso-used");
                AssertUtc(instant, used.LastModifiedUtc);
                AssertUtc(instant, used.LastUsedUtc);
                Assert.IsNull(page.Packages.Single(p => p.PackageId == "contoso-unknown").LastUsedUtc);
                var json = await ApiJsonAsync(store);
                AssertJsonUtc(json, "lastAttemptUtc", instant.AddHours(1));
                AssertJsonUtc(json, "lastAttemptCompletedUtc", instant.AddHours(1).AddMinutes(1));
                AssertJsonUtc(json, "lastSuccessfulImportUtc", instant.AddMinutes(1));
                var packages = (JArray)json["packages"];
                var usedJson = packages.Single(p => (string)p["packageId"] == "contoso-used");
                AssertJsonUtc(usedJson, "lastModifiedUtc", instant);
                AssertJsonUtc(usedJson, "lastUsedUtc", instant);
                Assert.IsTrue((bool)packages.Single(p => (string)p["packageId"] == "contoso-never")["knownNeverUsed"]);
                Assert.IsFalse((bool)packages.Single(p => (string)p["packageId"] == "contoso-unknown")["knownNeverUsed"]);

                await store.BeginImportAsync(instant.AddHours(2));
                var pending = await store.GetImportHealthAsync();
                AssertUtc(instant.AddHours(2), pending.LastAttemptUtc);
                Assert.IsNull(pending.LastAttemptCompletedUtc);
                AssertUtc(instant.AddMinutes(1), pending.LastSuccessfulImportUtc);
                var pendingJson = await ApiJsonAsync(store);
                AssertJsonUtc(pendingJson, "lastAttemptUtc", instant.AddHours(2));
                Assert.AreEqual(JTokenType.Null, pendingJson["lastAttemptCompletedUtc"].Type);
                AssertJsonUtc(pendingJson, "lastSuccessfulImportUtc", instant.AddMinutes(1));
            }
        }

        [TestMethod]
        public async Task ApiFakeStore_UnspecifiedAndUtcDatesHaveTheSameUtcContract_AndNullsRemainUnknown()
        {
            var instant = new DateTime(2026, 10, 1, 0, 15, 0, DateTimeKind.Utc);
            var store = new FixedStore
            {
                Health = new Agent365CatalogImportHealth
                {
                    LastAttemptUtc = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified),
                    LastAttemptCompletedUtc = instant,
                    LastSuccessfulImportUtc = instant,
                },
                Page = new Agent365PackageCatalogPage
                {
                    Packages = new[]
                    {
                        new Agent365Package
                        {
                            PackageId = "contoso-used", LastModifiedUtc = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified),
                            LastUsedUtc = instant, LastUsedDateTimeProvided = true,
                        },
                        new Agent365Package { PackageId = "contoso-unknown" },
                    },
                },
            };
            var json = await ApiJsonAsync(store);
            AssertJsonUtc(json, "lastAttemptUtc", instant);
            AssertJsonUtc(json, "lastAttemptCompletedUtc", instant);
            AssertJsonUtc(json, "lastSuccessfulImportUtc", instant);
            AssertJsonUtc(json["packages"][0], "lastModifiedUtc", instant);
            AssertJsonUtc(json["packages"][0], "lastUsedUtc", instant);
            Assert.AreEqual(JTokenType.Null, json["packages"][1]["lastUsedUtc"].Type);
            Assert.AreEqual(JTokenType.Null, json["packages"][1]["lastModifiedUtc"].Type);
            Assert.IsFalse((bool)json["packages"][1]["knownNeverUsed"]);

            var empty = await ApiJsonAsync(new FixedStore());
            Assert.AreEqual(JTokenType.Null, empty["lastAttemptUtc"].Type);
            Assert.AreEqual(JTokenType.Null, empty["lastAttemptCompletedUtc"].Type);
            Assert.AreEqual(JTokenType.Null, empty["lastSuccessfulImportUtc"].Type);
        }

        private static void AssertUtc(DateTime expected, DateTime? actual)
        {
            Assert.IsTrue(actual.HasValue);
            Assert.AreEqual(DateTimeKind.Utc, actual.Value.Kind);
            Assert.AreEqual(expected.Ticks, actual.Value.Ticks, "SQL UTC ticks must not be converted through the host's local zone.");
        }

        private static void AssertJsonUtc(JToken json, string field, DateTime expected)
        {
            var text = (string)json[field];
            StringAssert.EndsWith(text, "Z");
            var parsed = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
            Assert.AreEqual(expected, parsed.UtcDateTime);
            var madrid = TimeZoneInfo.ConvertTime(parsed, TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"));
            Assert.AreEqual(expected.AddHours(2).Date, madrid.Date, "A Madrid browser must not interpret UTC ticks as local time.");
            Assert.AreEqual(expected.Ticks, madrid.UtcDateTime.Ticks);
        }

        private static async Task<JObject> ApiJsonAsync(IAgent365PackageCatalogStore store)
        {
            using (var controller = new CatalogController(store)
            {
                Request = new HttpRequestMessage(HttpMethod.Get, "https://contoso.example/api/Agent365Catalog"),
                Configuration = new HttpConfiguration(),
            })
            {
                var result = await controller.Get();
                using (var response = await result.ExecuteAsync(CancellationToken.None))
                using (var reader = new JsonTextReader(new System.IO.StringReader(await response.Content.ReadAsStringAsync()))
                { DateParseHandling = DateParseHandling.None })
                {
                    return JObject.Load(reader);
                }
            }
        }

        private sealed class ScratchContext : AnalyticsEntitiesContext
        {
            static ScratchContext() { Database.SetInitializer<ScratchContext>(null); }
            public ScratchContext(string connectionString) : base(new SqlConnection(connectionString)) { }
        }

        private sealed class ScratchContextFactory : IAnalyticsDbContextFactory
        {
            private readonly string _connectionString;
            public ScratchContextFactory(string connectionString) { _connectionString = connectionString; }
            public AnalyticsEntitiesContext Create() => new ScratchContext(_connectionString);
        }

        private sealed class FixedStore : IAgent365PackageCatalogStore
        {
            public Agent365CatalogImportHealth Health { get; set; } = new Agent365CatalogImportHealth();
            public Agent365PackageCatalogPage Page { get; set; } = new Agent365PackageCatalogPage();
            public Task<Agent365CatalogImportHealth> GetImportHealthAsync() => Task.FromResult(Health);
            public Task<Agent365PackageCatalogPage> GetCurrentPageAsync(int offset, int pageSize, bool neverUsedOnly) => Task.FromResult(Page);
            public Task<Guid> BeginImportAsync(DateTime startedUtc) => throw new NotSupportedException();
            public Task SavePageAsync(Guid runId, IReadOnlyList<Agent365Package> packages) => throw new NotSupportedException();
            public Task CompleteImportAsync(Guid runId, DateTime completedUtc, int packageCount, int elementCount) => throw new NotSupportedException();
            public Task FailImportAsync(Guid runId, DateTime completedUtc, string error) => throw new NotSupportedException();
        }
    }
}

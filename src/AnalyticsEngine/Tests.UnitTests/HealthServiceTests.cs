extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using Common.Entities;
using Common.Entities.Entities.UsageReports;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the Health page's caching policy and its "Data overview" section building - the parts
    /// that used to be locked behind <c>static</c> methods over <c>new AnalyticsEntitiesContext()</c> and
    /// <c>MemoryCache.Default</c>. Everything here runs against <see cref="FakeHealthDataSource"/> and
    /// <see cref="InMemoryHealthCache"/>, so there is no SQL Server, App Insights or Service Bus
    /// dependency. See issue #379.
    ///
    /// The App Insights sections (liveness / exceptions / components) are deliberately not exercised:
    /// they would need a real telemetry endpoint, and their roll-up rules are already covered by
    /// <see cref="HealthRollupTests"/>.
    /// </summary>
    [TestClass]
    public class HealthServiceTests
    {
        private static HealthService Build(FakeHealthDataSource source, InMemoryHealthCache cache)
            => new HealthService(source, cache);

        #region Caching

        [TestMethod]
        public async Task Data_CachedResult_IsReturnedWithoutRequeryingTheSource()
        {
            var source = new FakeHealthDataSource();
            var cache = new InMemoryHealthCache();
            var service = Build(source, cache);

            var first = await service.LoadDataAsync();
            var second = await service.LoadDataAsync();

            Assert.AreEqual(1, source.CountsCallCount, "the second request must be served from cache");
            Assert.AreSame(first, second);
        }

        [TestMethod]
        public async Task Data_CacheExpired_RequeriesTheSource()
        {
            var source = new FakeHealthDataSource();
            var cache = new InMemoryHealthCache();
            var service = Build(source, cache);

            await service.LoadDataAsync();
            cache.Advance(TimeSpan.FromSeconds(HealthService.CacheSeconds + 1));
            await service.LoadDataAsync();

            Assert.AreEqual(2, source.CountsCallCount, "a stale Health page would hide a failure that has just started");
        }

        [TestMethod]
        public async Task Data_ResultIsCachedEvenWhenItCarriesAnError()
        {
            // Deliberate: caching failures too is what stops a broken database being hammered by every
            // page refresh. If this ever stops being true it should be a conscious decision.
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult { DataError = "Login failed for user 'analytics'." }
            };
            var cache = new InMemoryHealthCache();
            var service = Build(source, cache);

            var first = await service.LoadDataAsync();
            var second = await service.LoadDataAsync();

            Assert.AreEqual(HealthStatusNames.Unhealthy, first.Status);
            Assert.AreEqual(1, source.CountsCallCount);
            Assert.AreSame(first, second);
        }

        /// <summary>
        /// The per-section single-flight gates are instance state, so the API must serve every request
        /// from one shared instance. A per-request instance would let a burst of page opens stampede a
        /// cold section with N simultaneous builds - each of which scans the two biggest fact tables.
        /// This asserts the controller's own wiring, so replacing <c>HealthService.Default</c> with
        /// <c>new HealthService(...)</c> in that constructor fails here.
        /// </summary>
        [TestMethod]
        public void DefaultService_IsTheOneEveryRequestIsServedFrom()
        {
            var first = new HealthAPIController();
            var second = new HealthAPIController();

            Assert.AreSame(HealthService.Default, first.Service);
            Assert.AreSame(first.Service, second.Service, "two requests must not each get their own gates");
        }

        /// <summary>
        /// The section's <c>loadedAtUtc</c> is shown on the Health page and must mean "when this load
        /// started", as it always has. Stamping it after the scans would report a time tens of seconds
        /// later on a big tenant, where those scans run to their 20-second command timeout.
        /// </summary>
        [TestMethod]
        public async Task Data_LoadedAtIsStampedBeforeTheScans_NotAfterThem()
        {
            var source = new FakeHealthDataSource { CountsDelay = TimeSpan.FromMilliseconds(250) };
            var service = Build(source, new InMemoryHealthCache());

            var before = DateTime.UtcNow;
            var section = await service.LoadDataAsync();
            var after = DateTime.UtcNow;

            Assert.IsTrue(after - before >= TimeSpan.FromMilliseconds(200), "the fake must actually have been slow for this test to mean anything");
            Assert.IsTrue(section.LoadedAtUtc < before.AddMilliseconds(200),
                $"loadedAtUtc ({section.LoadedAtUtc:O}) was stamped after the scans, not before them (load started {before:O})");
        }

        #endregion

        #region Data section - failure handling

        [TestMethod]
        public async Task Data_DatabaseUnreachable_IsUnhealthyWithoutThrowing()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult { DataError = "A network-related or instance-specific error occurred." }
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.AreEqual(HealthStatusNames.Unhealthy, section.Status);
            Assert.AreEqual(1, section.Reasons.Count);
            StringAssert.Contains(section.Reasons[0], "A network-related or instance-specific error occurred.");
        }

        /// <summary>
        /// A hard connection failure means there is nothing to scan; firing the two heavy fact-table
        /// scans anyway would just make an already-broken page slower.
        /// </summary>
        [TestMethod]
        public async Task Data_DatabaseUnreachable_SkipsTheHeavyVolumeScans()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult { DataError = "database is unreachable" }
            };
            var service = Build(source, new InMemoryHealthCache());

            await service.LoadDataAsync();

            Assert.AreEqual(0, source.RecentVolumeRequests.Count);
        }

        [TestMethod]
        public async Task Data_ScansBothFactTables_WhenTheDatabaseIsReachable()
        {
            var source = new FakeHealthDataSource();
            var service = Build(source, new InMemoryHealthCache());

            await service.LoadDataAsync();

            CollectionAssert.AreEquivalent(new[] { "hits", "audit_events" }, source.RecentVolumeRequests);
        }

        /// <summary>
        /// The DMV counts need VIEW DATABASE STATE, which a locked-down SQL login won't have. Losing them
        /// must not take the rest of the section - especially the "is data still flowing" figures - with it.
        /// </summary>
        [TestMethod]
        public async Task Data_ApproximateCountsUnavailable_KeepsTheVolumeAndTrackedTeamsFigures()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult
                {
                    CountsError = "VIEW DATABASE STATE permission was denied",
                    TeamsBeingTrackedCount = 7,
                }
            };
            source.RecentVolumeByTable["hits"] = new RecentVolumeResult { Last24h = 10, Last7d = 70, Newest = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
            source.RecentVolumeByTable["audit_events"] = new RecentVolumeResult { Last24h = 3, Last7d = 21 };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            Assert.IsTrue(section.Reasons.Any(r => r.StartsWith("Approximate counts unavailable:")));
            Assert.AreEqual(7, section.TeamsBeingTrackedCount);
            Assert.AreEqual(10, section.HitsLast24h);
            Assert.AreEqual(70, section.HitsLast7d);
            Assert.AreEqual(3, section.AuditEventsLast24h);
            Assert.IsNull(section.RecentVolumeError);
        }

        /// <summary>
        /// ...and the other way round: the bounded volume scans are the ones that time out on a very
        /// large tenant, and losing them must not blank the cheap DMV counts.
        /// </summary>
        [TestMethod]
        public async Task Data_VolumeScanFails_KeepsTheApproximateCountsAndLeavesVolumesUnset()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult { ActivityCount = 12_000_000, HitCount = 500_000, DatabaseSizeMb = 4096 }
            };
            source.RecentVolumeByTable["hits"] = new RecentVolumeResult { Error = "Execution Timeout Expired." };
            source.RecentVolumeByTable["audit_events"] = new RecentVolumeResult { Last24h = 3, Last7d = 21 };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            Assert.AreEqual(12_000_000, section.ActivityCount);
            Assert.AreEqual(4096, section.DatabaseSizeMb);
            StringAssert.Contains(section.RecentVolumeError, "Execution Timeout Expired.");
            Assert.IsNull(section.HitsLast24h, "a timed-out scan must not be reported as a real zero");
            Assert.AreEqual(3, section.AuditEventsLast24h, "the table that did scan still reports");
        }

        [TestMethod]
        public async Task Data_BothVolumeScansFailWithTheSameError_ReportsItOnce()
        {
            var source = new FakeHealthDataSource();
            source.RecentVolumeByTable["hits"] = new RecentVolumeResult { Error = "Execution Timeout Expired." };
            source.RecentVolumeByTable["audit_events"] = new RecentVolumeResult { Error = "Execution Timeout Expired." };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.AreEqual("Execution Timeout Expired.", section.RecentVolumeError);
        }

        [TestMethod]
        public async Task Data_EverythingLoads_IsHealthy()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult { ActivityCount = 10, UserCount = 200_000 }
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.AreEqual(HealthStatusNames.Healthy, section.Status);
            CollectionAssert.AreEqual(new[] { "All checks passing." }, section.Reasons);
            Assert.IsTrue(section.CountsAreApproximate);
        }

        #endregion

        #region Data section - Copilot usage-report imports

        [TestMethod]
        public async Task Data_ConcealedIdentities_AreSurfacedAsDegradedWithTheAdminFix()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult
                {
                    CopilotUsageReportImports = new List<CopilotUsageReportImportRow>
                    {
                        new CopilotUsageReportImportRow
                        {
                            ReportName = CopilotUsageReportNames.UsageUserDetail,
                            ImportedUtc = new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc),
                            IsUpnObfuscated = true,
                        }
                    }
                }
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.IsTrue(section.CopilotUsageReportsIdentitiesConcealed);
            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            // Without the admin-centre instruction this reads exactly like "no Copilot usage in this tenant".
            Assert.IsTrue(section.Reasons.Any(r => r.Contains("Display concealed user, group and site names in all reports")));
        }

        [TestMethod]
        public async Task Data_ConcealedIdentitiesOnADifferentReport_IsNotFlagged()
        {
            // Only the per-user detail report can't be linked to a user when identities are concealed.
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult
                {
                    CopilotUsageReportImports = new List<CopilotUsageReportImportRow>
                    {
                        new CopilotUsageReportImportRow
                        {
                            ReportName = "SomeOtherReport",
                            ImportedUtc = new DateTime(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc),
                            IsUpnObfuscated = true,
                        }
                    }
                }
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.IsFalse(section.CopilotUsageReportsIdentitiesConcealed);
            Assert.AreEqual(HealthStatusNames.Healthy, section.Status);
        }

        [TestMethod]
        public async Task Data_CopilotImportErrors_AreListedAndDegradeTheSection()
        {
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult
                {
                    CopilotUsageReportImports = new List<CopilotUsageReportImportRow>
                    {
                        new CopilotUsageReportImportRow { ReportName = "ReportA", ImportedUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), Error = "403 Forbidden" },
                        new CopilotUsageReportImportRow { ReportName = "ReportB", ImportedUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
                    }
                }
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            CollectionAssert.AreEqual(new[] { "ReportA: 403 Forbidden" }, section.CopilotUsageReportErrors);
            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            Assert.IsTrue(section.Reasons.Any(r => r.Contains("ReportA: 403 Forbidden")));
        }

        [TestMethod]
        public async Task Data_LastCopilotImport_IsTheNewestAcrossReports()
        {
            var newest = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var source = new FakeHealthDataSource
            {
                CountsResult = new DatabaseCountsResult
                {
                    CopilotUsageReportImports = new List<CopilotUsageReportImportRow>
                    {
                        new CopilotUsageReportImportRow { ReportName = "ReportA", ImportedUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                        new CopilotUsageReportImportRow { ReportName = "ReportB", ImportedUtc = newest },
                        new CopilotUsageReportImportRow { ReportName = "ReportC", ImportedUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) },
                    }
                }
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.AreEqual(newest, section.CopilotUsageReportLastImportUtc);
        }

        [TestMethod]
        public async Task Data_NoCopilotImportsRecorded_LeavesTheImportFieldsUnset()
        {
            var service = Build(new FakeHealthDataSource(), new InMemoryHealthCache());

            var section = await service.LoadDataAsync();

            Assert.IsNull(section.CopilotUsageReportLastImportUtc);
            Assert.IsFalse(section.CopilotUsageReportsIdentitiesConcealed);
            Assert.AreEqual(0, section.CopilotUsageReportErrors.Count);
        }

        #endregion

        #region Overview probe row

        [TestMethod]
        public void OverviewDataRow_ReportsUnhealthyWhenTheProbeFailed()
        {
            var failed = HealthDataSectionRules.DataProbeStatus("Login failed for user 'analytics'.");

            Assert.AreEqual("data", failed.Key);
            Assert.AreEqual(HealthStatusNames.Unhealthy, failed.Status);
            StringAssert.Contains(failed.Reasons.Single(), "Login failed for user 'analytics'.");
        }

        [TestMethod]
        public void OverviewDataRow_ReportsHealthyWhenTheProbeSucceeded()
        {
            var ok = HealthDataSectionRules.DataProbeStatus(null);

            Assert.AreEqual("data", ok.Key);
            Assert.AreEqual(HealthStatusNames.Healthy, ok.Status);
            StringAssert.Contains(ok.Reasons.Single(), "Database reachable");
        }

        #endregion

        #region Config section

        [TestMethod]
        public async Task Config_SchemaReadFailure_IsReportedWithoutLosingTheRestOfTheSection()
        {
            var source = new FakeHealthDataSource
            {
                PendingMigrationsException = new InvalidOperationException("Cannot open database"),
                CallWebhookStatus = new CallWebhookStatusResult { CallsImportEnabled = true, WebhookState = "Active" },
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadConfigAsync(new TestsAppConfig());

            StringAssert.Contains(section.SchemaError, "Cannot open database");
            Assert.IsNull(section.SchemaUpToDate, "an unreadable schema is 'unknown', not 'up to date'");
            Assert.IsTrue(section.CallsImportEnabled, "the webhook block still ran");
            Assert.AreEqual("Active", section.WebhookState);
            Assert.AreNotEqual(HealthStatusNames.Healthy, section.Status);
            Assert.IsTrue(section.Reasons.Contains("Some configuration couldn't be read."));
        }

        [TestMethod]
        public async Task Config_WebhookReadFailure_IsReportedAsAConfigError()
        {
            var source = new FakeHealthDataSource
            {
                PendingMigrations = new List<string>(),
                CallWebhookStatusException = new InvalidOperationException("Graph call failed"),
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadConfigAsync(new TestsAppConfig());

            StringAssert.Contains(section.ConfigError, "Graph call failed");
            Assert.AreEqual(true, section.SchemaUpToDate, "the schema check still ran and found nothing pending");
            Assert.AreNotEqual(HealthStatusNames.Healthy, section.Status);
        }

        [TestMethod]
        public async Task Config_PendingMigrations_MeanTheSchemaIsBehindTheBuild()
        {
            var source = new FakeHealthDataSource
            {
                PendingMigrations = new List<string> { "202601010000001_SomethingNew" },
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadConfigAsync(new TestsAppConfig());

            Assert.AreEqual(false, section.SchemaUpToDate);
            CollectionAssert.AreEqual(new[] { "202601010000001_SomethingNew" }, section.PendingMigrations);
            Assert.IsNull(section.SchemaError);
        }

        [TestMethod]
        public async Task Config_EveryImportToggleHasAnEnabledImportBadge()
        {
            var importToggleNames = typeof(ImportTaskSettings)
                .GetProperties()
                .Where(property => property.PropertyType == typeof(bool)
                                   && Attribute.IsDefined(
                                       property,
                                       typeof(ImportTaskSettings.ImportPropAttribute)))
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray();

            CollectionAssert.AreEqual(
                importToggleNames,
                HealthService.ImportLabelsBySettingProperty.Keys.OrderBy(name => name).ToArray(),
                "Every public bool ImportTaskSettings import toggle must have a Health page label.");

            var config = new TestsAppConfig
            {
                ImportJobSettings = new ImportTaskSettings(),
            };
            foreach (var propertyName in importToggleNames)
            {
                typeof(ImportTaskSettings).GetProperty(propertyName)
                    .SetValue(config.ImportJobSettings, true);
            }

            var source = new FakeHealthDataSource
            {
                PendingMigrations = new List<string>(),
            };
            var service = Build(source, new InMemoryHealthCache());

            var section = await service.LoadConfigAsync(config);

            CollectionAssert.AreEquivalent(
                HealthService.ImportLabelsBySettingProperty.Values.ToArray(),
                section.EnabledImports.ToArray());
            CollectionAssert.Contains(
                section.EnabledImports,
                "Copilot AI interaction history (tenant-wide unless scoped)");
        }

        #endregion

        #region Constructor guards

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void Constructor_RejectsAMissingDataSource()
        {
            new HealthService(null, new InMemoryHealthCache());
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void Constructor_RejectsAMissingCache()
        {
            new HealthService(new FakeHealthDataSource(), null);
        }

        #endregion
    }
}

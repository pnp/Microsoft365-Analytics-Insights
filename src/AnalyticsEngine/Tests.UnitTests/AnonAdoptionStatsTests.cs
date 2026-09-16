using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UsageReporting;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.StatsUploader;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the anonymised Copilot / licence adoption block uploaded with the telemetry report.
    ///
    /// The single most important test here is
    /// <see cref="Mapper_TenantDataNeverReachesThePayload"/>: everything else checks a rule, but that
    /// one checks the OUTCOME the rules exist for, and would catch a future field that quietly starts
    /// forwarding customer text.
    /// </summary>
    [TestClass]
    public class AnonAdoptionStatsTests
    {
        // Obviously-synthetic stand-ins for the tenant data the source models really carry. The Greek
        // is deliberate: a department or agent name routinely contains non-Latin text, and a substring
        // search for it in the serialised payload is an exact, unambiguous leak detector.
        private const string GreekDepartment = "Καλημέρα κόσμε";
        private const string SecretCountry = "Contosoland";
        private const string SecretAgentName = "Contoso Expenses Assistant";
        private const string SecretUpn = "jane.doe@contoso.onmicrosoft.com";
        private const string SecretSqlError = "Invalid object name 'ContosoProd.dbo.copilot_chats'";
        private const string ResellerSku = "CONTOSO_PARTNER_BUNDLE";

        #region The load-bearing privacy test

        [TestMethod]
        public void Mapper_TenantDataNeverReachesThePayload()
        {
            var summary = TenantShapedSummary();
            var overview = TenantShapedOverview();

            var mapped = AnonAdoptionStatsMapper.Map(
                summary, overview, AllowList("ENTERPRISEPACK", "Microsoft_365_Copilot"), DateTime.UtcNow, 28);

            var json = JsonConvert.SerializeObject(mapped);

            foreach (var secret in new[]
            {
                GreekDepartment, SecretCountry, SecretAgentName, SecretUpn, SecretSqlError, ResellerSku,
                "Legal", "Contoso",
            })
            {
                Assert.IsFalse(
                    json.IndexOf(secret, StringComparison.OrdinalIgnoreCase) >= 0,
                    $"Tenant data leaked into the anonymous telemetry payload: '{secret}' was found in {json}");
            }
        }

        [TestMethod]
        public void Mapper_WarningTextIsReplacedByACount()
        {
            var summary = TenantShapedSummary();
            summary.Warnings = new List<string> { SecretSqlError, "Could not load agents: timeout" };

            var mapped = AnonAdoptionStatsMapper.Map(summary, null, AllowList(), DateTime.UtcNow, 28);

            Assert.AreEqual(2, mapped.Copilot.WarningCount);
            Assert.IsFalse(
                JsonConvert.SerializeObject(mapped).Contains("ContosoProd"),
                "Warning strings embed InnermostMessage(ex), which can carry database and object names.");
        }

        #endregion

        #region Bucketing

        [DataTestMethod]
        [DataRow(0L, 0L)]          // Exact: "nobody uses this" is the most useful answer in the payload.
        [DataRow(1L, 5L)]
        [DataRow(9L, 5L)]
        [DataRow(10L, 10L)]
        [DataRow(14L, 10L)]
        [DataRow(15L, 20L)]
        [DataRow(99L, 100L)]
        [DataRow(124L, 100L)]
        [DataRow(125L, 150L)]
        [DataRow(999L, 1000L)]
        [DataRow(1049L, 1000L)]
        [DataRow(1050L, 1100L)]
        [DataRow(9999L, 10000L)]
        [DataRow(10499L, 10000L)]
        [DataRow(10500L, 11000L)]
        [DataRow(99999L, 100000L)]
        [DataRow(204999L, 200000L)]
        [DataRow(205000L, 210000L)]
        public void Bucket_RoundsToTheBandForItsScale(long input, long expected)
        {
            Assert.AreEqual(expected, AnonStatsPrivacy.Bucket(input));
        }

        [TestMethod]
        public void Bucket_NullAndNegativeBecomeNull()
        {
            Assert.IsNull(AnonStatsPrivacy.Bucket((long?)null));
            Assert.IsNull(AnonStatsPrivacy.Bucket(-1L), "A negative count is nonsense; it must not reach the wire.");
        }

        [TestMethod]
        public void Bucket_NeverTurnsANonZeroPopulationIntoZero()
        {
            // The distinction that matters most: a tenant with a handful of users must not be reported
            // as having none, because "zero" is read as a real, meaningful answer everywhere else.
            for (var i = 1L; i < 10; i++)
            {
                Assert.AreNotEqual(0L, AnonStatsPrivacy.Bucket(i));
            }
        }

        [TestMethod]
        public void RoundPct_GoesToTheNearestHalfPoint()
        {
            Assert.AreEqual(12.5, AnonStatsPrivacy.RoundPct(12.6));
            Assert.AreEqual(13.0, AnonStatsPrivacy.RoundPct(12.8));
            Assert.IsNull(AnonStatsPrivacy.RoundPct(double.NaN));
            Assert.IsNull(AnonStatsPrivacy.RoundPct(double.PositiveInfinity));
        }

        #endregion

        #region Suppression

        [TestMethod]
        public void Map_TenantBelowTheSeatFloorIsSuppressed()
        {
            var summary = TenantShapedSummary();
            summary.LicensedUsers = AnonStatsPrivacy.MinimumTenantSeats - 1;

            var mapped = AnonAdoptionStatsMapper.Map(summary, null, AllowList(), DateTime.UtcNow, 28);

            Assert.IsTrue(mapped.Suppressed);
            Assert.AreEqual(AnonStatsPrivacy.SuppressionReasons.BelowMinimumPopulation, mapped.SuppressionReason);
            Assert.IsNull(mapped.Copilot, "A suppressed tenant must not carry any Copilot figures at all.");
        }

        [TestMethod]
        public void Map_TenantExactlyOnTheSeatFloorIsReported()
        {
            var summary = TenantShapedSummary();
            summary.LicensedUsers = AnonStatsPrivacy.MinimumTenantSeats;

            var mapped = AnonAdoptionStatsMapper.Map(summary, null, AllowList(), DateTime.UtcNow, 28);

            Assert.IsFalse(mapped.Suppressed);
            Assert.IsNotNull(mapped.Copilot);
        }

        [TestMethod]
        public void Map_SuppressedTenantStillReportsItsLicenceEstate()
        {
            // Suppression is about the Copilot seat population being too small to describe. It says
            // nothing about the tenant's wider licence estate, which has its own per-SKU threshold.
            var summary = TenantShapedSummary();
            summary.LicensedUsers = 3;

            var mapped = AnonAdoptionStatsMapper.Map(
                summary, TenantShapedOverview(), AllowList("ENTERPRISEPACK"), DateTime.UtcNow, 28);

            Assert.IsTrue(mapped.Suppressed);
            Assert.IsNotNull(mapped.Licences);
            Assert.IsTrue(mapped.Licences.Skus.Any(s => s.SkuPartNumber == "ENTERPRISEPACK"));
        }

        [TestMethod]
        public void Map_NoSummaryIsReportedAsUnavailableRatherThanEmpty()
        {
            var mapped = AnonAdoptionStatsMapper.Map(null, null, AllowList(), DateTime.UtcNow, 28);

            Assert.IsTrue(mapped.Suppressed);
            Assert.AreEqual(AnonStatsPrivacy.SuppressionReasons.NotAvailable, mapped.SuppressionReason);
        }

        #endregion

        #region SKUs

        [TestMethod]
        public void Map_UnpublishedSkuIsNotNamed()
        {
            var overview = new LicenceActivityOverview
            {
                DistinctAssignedUsers = 500,
                Licences = new List<LicenceActivitySku>
                {
                    Sku(ResellerSku, 400),
                },
            };

            var mapped = AnonAdoptionStatsMapper.Map(null, overview, AllowList("ENTERPRISEPACK"), DateTime.UtcNow, 28);

            var row = mapped.Licences.Skus.Single();
            Assert.AreEqual(AnonStatsPrivacy.UnlistedSku, row.SkuPartNumber);
        }

        [TestMethod]
        public void Map_SmallSkusRollUpIntoOther()
        {
            var overview = new LicenceActivityOverview
            {
                Licences = new List<LicenceActivitySku>
                {
                    Sku("ENTERPRISEPACK", 300),
                    Sku("POWER_BI_PRO", 4),
                    Sku("VISIOCLIENT", 6),
                    Sku("PROJECTPROFESSIONAL", 5),
                },
            };

            var mapped = AnonAdoptionStatsMapper.Map(
                null, overview,
                AllowList("ENTERPRISEPACK", "POWER_BI_PRO", "VISIOCLIENT", "PROJECTPROFESSIONAL"),
                DateTime.UtcNow, 28);

            var other = mapped.Licences.Skus.Single(s => s.SkuPartNumber == AnonStatsPrivacy.OtherSkuBucket);
            Assert.AreEqual(3, other.RolledUpSkuCount);

            // 4 + 6 + 5 = 15 raw, summed BEFORE bucketing, then bucketed to the nearest 10.
            Assert.AreEqual(20L, other.AssignedUsers);

            Assert.IsFalse(
                mapped.Licences.Skus.Any(s => s.SkuPartNumber == "POWER_BI_PRO"),
                "A SKU below the per-SKU floor must not appear on its own.");
        }

        [TestMethod]
        public void Map_OtherBucketIsDroppedWhenItIsItselfTooSmall()
        {
            // Otherwise three two-seat SKUs would simply be reported as one six-seat row, which is the
            // exact disclosure the per-SKU floor exists to prevent.
            var overview = new LicenceActivityOverview
            {
                Licences = new List<LicenceActivitySku>
                {
                    Sku("POWER_BI_PRO", 2),
                    Sku("VISIOCLIENT", 2),
                    Sku("PROJECTPROFESSIONAL", 2),
                },
            };

            var mapped = AnonAdoptionStatsMapper.Map(
                null, overview, AllowList("POWER_BI_PRO", "VISIOCLIENT", "PROJECTPROFESSIONAL"), DateTime.UtcNow, 28);

            Assert.AreEqual(0, mapped.Licences.Skus.Count);
        }

        [TestMethod]
        public void SkuAllowList_NullIsNotPublishedAndDoesNotThrow()
        {
            // OfficeLicenseNameResolver.GetDisplayNameFor calls id.ToLower() unguarded.
            var allowList = new EmbeddedCsvSkuAllowList(new StubResolver("ENTERPRISEPACK"));

            Assert.IsFalse(allowList.IsPublished(null));
            Assert.IsFalse(allowList.IsPublished(string.Empty));
            Assert.IsFalse(allowList.IsPublished("   "));
            Assert.IsTrue(allowList.IsPublished("enterprisepack"), "Matching must be case-insensitive.");
        }

        [TestMethod]
        public void SkuAllowList_RealCsvRecognisesAKnownSkuAndRejectsAMadeUpOne()
        {
            var allowList = new EmbeddedCsvSkuAllowList();

            Assert.IsTrue(allowList.IsPublished("ENTERPRISEPACK"));
            Assert.IsFalse(allowList.IsPublished(ResellerSku));
        }

        #endregion

        #region Label allow-lists

        [TestMethod]
        public void Map_UnrecognisedLabelsAreDroppedNotForwarded()
        {
            var summary = TenantShapedSummary();
            summary.BandBreakdown = new List<AdoptionCategory>
            {
                new AdoptionCategory { Label = "Champion", Value = 12 },
                new AdoptionCategory { Label = GreekDepartment, Value = 40 },
            };

            var mapped = AnonAdoptionStatsMapper.Map(summary, null, AllowList(), DateTime.UtcNow, 28);

            Assert.AreEqual(1, mapped.Copilot.BandBreakdown.Count);
            Assert.AreEqual("Champion", mapped.Copilot.BandBreakdown[0].Label);
        }

        [TestMethod]
        public void Map_CoverageRowWithAnUnknownWorkloadIsDropped()
        {
            var overview = new LicenceActivityOverview
            {
                Coverage = new List<LicenceActivityCoverage>
                {
                    new LicenceActivityCoverage { Workload = "teams", Status = "available", Source = "microsoftGraphUsageReport" },
                    new LicenceActivityCoverage { Workload = GreekDepartment, Status = "available" },
                },
            };

            var mapped = AnonAdoptionStatsMapper.Map(null, overview, AllowList(), DateTime.UtcNow, 28);

            Assert.AreEqual(1, mapped.Licences.Coverage.Count);
            Assert.AreEqual("teams", mapped.Licences.Coverage[0].Workload);
        }

        [TestMethod]
        public void Map_CoverageFreeTextIsNeverCopied()
        {
            var overview = new LicenceActivityOverview
            {
                Coverage = new List<LicenceActivityCoverage>
                {
                    new LicenceActivityCoverage
                    {
                        Workload = "copilot",
                        Status = "partial",
                        Message = SecretSqlError,
                        SnapshotDates = new List<DateTime> { new DateTime(2031, 3, 20) },
                    },
                },
            };

            var mapped = AnonAdoptionStatsMapper.Map(null, overview, AllowList(), DateTime.UtcNow, 28);
            var json = JsonConvert.SerializeObject(mapped);

            Assert.IsFalse(json.Contains("ContosoProd"));
            Assert.IsFalse(json.Contains("2031-03-20"), "Snapshot dates pin the tenant's import history to the day.");
        }

        #endregion

        #region Client id rotation

        [TestMethod]
        public void ClientId_LegacyTenantHashIsRetiredOnce()
        {
            var tenantId = Guid.NewGuid();
            var legacy = AnonUsageStatsModelLoader.LegacyAnonClientId(tenantId);

            var replacement = AnonUsageStatsModelLoader.ResolveAnonClientId(tenantId, legacy);

            Assert.AreNotEqual(legacy, replacement);
            Assert.AreNotEqual(
                AnonUsageStatsModelLoader.LegacyAnonClientId(tenantId), replacement,
                "The replacement must not be derivable from the tenant id - that is the whole point.");
        }

        [TestMethod]
        public void ClientId_AnIdWeAlreadyMintedIsKept()
        {
            var tenantId = Guid.NewGuid();
            var existing = Guid.NewGuid().ToString("N");

            Assert.AreEqual(existing, AnonUsageStatsModelLoader.ResolveAnonClientId(tenantId, existing));
        }

        [TestMethod]
        public void ClientId_NoPreviousReportMintsAFreshId()
        {
            var tenantId = Guid.NewGuid();

            var first = AnonUsageStatsModelLoader.ResolveAnonClientId(tenantId, null);
            var second = AnonUsageStatsModelLoader.ResolveAnonClientId(tenantId, string.Empty);

            Assert.IsFalse(string.IsNullOrWhiteSpace(first));
            Assert.AreNotEqual(first, second, "Each mint is random, not derived from the tenant.");
            Assert.AreNotEqual(AnonUsageStatsModelLoader.LegacyAnonClientId(tenantId), first);
        }

        #endregion

        #region Server-side merge

        [TestMethod]
        public void UpdateWith_NullAdoptionKeepsWhatTheServerAlreadyHad()
        {
            var stored = new AnonUsageStatsModel
            {
                AnonClientId = "client",
                Generated = DateTime.UtcNow.AddDays(-1),
                Adoption = new AnonAdoptionStats { WindowDays = 28 },
            };

            var update = new AnonUsageStatsModel { AnonClientId = "client", Generated = DateTime.UtcNow };

            stored.UpdateWith(update);

            Assert.IsNotNull(stored.Adoption, "A client that stops sending a block must not blank the dashboard.");
            Assert.AreEqual(28, stored.Adoption.WindowDays);
        }

        [TestMethod]
        public void UpdateWith_NewAdoptionReplacesTheOldOne()
        {
            var stored = new AnonUsageStatsModel
            {
                AnonClientId = "client",
                Generated = DateTime.UtcNow.AddDays(-1),
                Adoption = new AnonAdoptionStats { WindowDays = 7 },
            };

            var update = new AnonUsageStatsModel
            {
                AnonClientId = "client",
                Generated = DateTime.UtcNow,
                Adoption = new AnonAdoptionStats { WindowDays = 28 },
            };

            stored.UpdateWith(update);

            Assert.AreEqual(28, stored.Adoption.WindowDays);
        }

        #endregion

        #region Collector gating

        [TestMethod]
        public async Task Collector_DoesNothingWhenTheDeploymentCannotMeasureAdoption()
        {
            // No user metadata import: there is no way to know who holds a seat.
            var settings = new ImportTaskSettings { GraphUsersMetadata = false, Copilot = true };
            var source = new StubAnalysisSource();
            var collector = Collector(source, new InMemoryImportLastRunStore(), settings);

            Assert.IsNull(await collector.GetAdoptionStats(null));
            Assert.AreEqual(0, source.CallCount);
        }

        [TestMethod]
        public async Task Collector_RunsWeeklyNotDaily()
        {
            var settings = MeasurableSettings();
            var store = new InMemoryImportLastRunStore();
            var source = new StubAnalysisSource();
            var collector = Collector(source, store, settings);

            Assert.IsNotNull(await collector.GetAdoptionStats(null));
            Assert.AreEqual(1, source.CallCount);

            // Same day: not due.
            Assert.IsNull(await collector.GetAdoptionStats(null));
            Assert.AreEqual(1, source.CallCount);

            // Eight days ago: due again.
            await store.SetLastRunUtc(AdoptionStatsCollector.LastRunKey, DateTime.UtcNow.AddDays(-8));
            Assert.IsNotNull(await collector.GetAdoptionStats(null));
            Assert.AreEqual(2, source.CallCount);
        }

        [TestMethod]
        public async Task Collector_FailedAnalysisDoesNotConsumeTheWeeklySlot()
        {
            var store = new InMemoryImportLastRunStore();
            var source = new StubAnalysisSource { Throw = true };
            var collector = Collector(source, store, MeasurableSettings());

            Assert.IsNull(await collector.GetAdoptionStats(null), "A failure must fail soft, not propagate.");
            Assert.IsNull(
                await store.GetLastRunUtc(AdoptionStatsCollector.LastRunKey),
                "Stamping the gate on failure would block retries for a whole week.");

            source.Throw = false;
            Assert.IsNotNull(await collector.GetAdoptionStats(null), "The next cycle must retry.");
        }

        #endregion

        #region Helpers

        private static AdoptionStatsCollector Collector(
            IAdoptionAnalysisSource source, IImportLastRunStore store, ImportTaskSettings settings)
        {
            return new AdoptionStatsCollector(source, store, AllowList("ENTERPRISEPACK"), settings, null);
        }

        private static ImportTaskSettings MeasurableSettings()
            => new ImportTaskSettings { GraphUsersMetadata = true, Copilot = true };

        private static ISkuAllowList AllowList(params string[] published)
            => new EmbeddedCsvSkuAllowList(new StubResolver(published));

        private static LicenceActivitySku Sku(string partNumber, int assignedUsers)
        {
            return new LicenceActivitySku
            {
                SkuId = partNumber,
                // Set deliberately: the display name must never be copied to the payload, and for an
                // unrecognised SKU it falls back to the raw part number anyway.
                Name = "Contoso Bundled " + partNumber,
                AssignedUsers = assignedUsers,
                Workloads = new List<LicenceActivityDistribution>
                {
                    new LicenceActivityDistribution
                    {
                        Workload = "teams", High = assignedUsers / 2, Moderate = 1, Low = 1, Zero = 1, Unknown = 1,
                    },
                },
            };
        }

        /// <summary>A summary stuffed with the kinds of tenant data the real analysis carries.</summary>
        private static CopilotAdoptionSummary TenantShapedSummary()
        {
            return new CopilotAdoptionSummary
            {
                GeneratedUtc = DateTime.UtcNow,
                WindowDays = 28,
                LicensedUsers = 1200,
                ScoredUsers = 1180,
                ActiveUsers = 640,
                HabitualUsers = 210,
                NeverUsedUsers = 400,
                DormantUsers = 140,
                AdoptionRatePct = 54.27,
                HabitRatePct = 17.8,
                AverageAdoptionScore = 41.6,
                MedianAdoptionScore = 38.2,
                TotalInteractions = 128_455,
                ReclaimableSeats = 310,
                DisabledLicensedUsers = 12,
                UnlicensedActiveUsers = 85,
                RecommendedForLicence = 40,
                CoworkUsers = 90,
                CoworkAdoptionPct = 7.5,
                CoworkInteractions = 4300,
                FiguresIncomplete = true,

                Warnings = new List<string> { SecretSqlError },
                IncompleteReasons = new List<string> { SecretSqlError },

                BandBreakdown = new List<AdoptionCategory>
                {
                    new AdoptionCategory { Label = "Champion", Value = 120 },
                    new AdoptionCategory { Label = "Never used", Value = 400 },
                },
                Funnel = new List<AdoptionCategory>
                {
                    new AdoptionCategory { Label = "Licensed", Value = 1180 },
                    new AdoptionCategory { Label = "Champions", Value = 120 },
                },
                HabitBuckets = new List<AdoptionHabitBucket>
                {
                    new AdoptionHabitBucket { Label = "Daily", RangeLabel = "20+ active days a month", Users = 90 },
                },

                // Every one of these carries tenant text and must be dropped wholesale.
                AdoptionByDepartment = new List<AdoptionSegmentRow>
                {
                    new AdoptionSegmentRow { Segment = GreekDepartment, LicensedUsers = 30, ActiveUsers = 12 },
                    new AdoptionSegmentRow { Segment = "Legal", LicensedUsers = 20, ActiveUsers = 4 },
                },
                AdoptionByCountry = new List<AdoptionSegmentRow>
                {
                    new AdoptionSegmentRow { Segment = SecretCountry, LicensedUsers = 50, ActiveUsers = 20 },
                },
                IntensityByDepartment = new List<AdoptionIntensityPoint>
                {
                    new AdoptionIntensityPoint { Segment = GreekDepartment, LicensedUsers = 30 },
                },
                OpportunityByDepartment = new List<AdoptionCategory>
                {
                    new AdoptionCategory { Label = GreekDepartment, Value = 9 },
                },
                UsageByApp = new List<AdoptionCategory>
                {
                    new AdoptionCategory { Label = SecretAgentName, Value = 20 },
                },

                DataSources = new AdoptionDataSources
                {
                    AuditAvailable = true,
                    UserMetadataAvailable = true,
                    CopilotUsageReportAvailable = true,
                },

                Agents = new AgentEstateSummary
                {
                    KnownAgents = 24,
                    ActiveAgents = 11,
                    CustomAgents = 6,
                    AgentUsers = 310,
                    AgentInteractions = 15_400,
                    MostPopularAgent = SecretAgentName,
                    MostVersatileAgent = SecretAgentName,
                    UsageByAgent = new List<AdoptionCategory>
                    {
                        new AdoptionCategory { Label = SecretAgentName, Value = 400 },
                    },
                    UsageByDepartment = new List<AdoptionCategory>
                    {
                        new AdoptionCategory { Label = GreekDepartment, Value = 88 },
                    },
                },
            };
        }

        private static LicenceActivityOverview TenantShapedOverview()
        {
            return new LicenceActivityOverview
            {
                DistinctAssignedUsers = 1500,
                Licences = new List<LicenceActivitySku>
                {
                    Sku("ENTERPRISEPACK", 1400),
                    Sku("Microsoft_365_Copilot", 1200),
                    Sku(ResellerSku, 300),
                },
                Coverage = new List<LicenceActivityCoverage>
                {
                    new LicenceActivityCoverage
                    {
                        Workload = "teams",
                        Status = "available",
                        Source = "microsoftGraphUsageReport",
                        Granularity = "weeklySupportingSnapshot",
                        Message = SecretSqlError,
                        ExpectedSamples = 4,
                        ObservedSamples = 4,
                        UnmatchedUsers = 7,
                    },
                },
                Departments = new List<LicenceActivityDemographic>
                {
                    new LicenceActivityDemographic { Id = 1, Name = GreekDepartment, AssignedUsers = 30 },
                    new LicenceActivityDemographic { Id = 2, Name = "Legal", AssignedUsers = 20 },
                },
                Countries = new List<LicenceActivityDemographic>
                {
                    new LicenceActivityDemographic { Id = 3, Name = SecretCountry, AssignedUsers = 60 },
                },
                Messages = new List<string> { $"Could not match {SecretUpn}" },
            };
        }

        private sealed class StubResolver : IOfficeLicenseNameResolver
        {
            private readonly HashSet<string> _published;

            internal StubResolver(params string[] published)
            {
                _published = new HashSet<string>(published, StringComparer.OrdinalIgnoreCase);
            }

            public string GetDisplayNameFor(string id)
            {
                if (id == null) throw new ArgumentNullException(nameof(id), "The real resolver throws here too.");
                return _published.Contains(id) ? "Some Microsoft Product" : null;
            }
        }

        private sealed class StubAnalysisSource : IAdoptionAnalysisSource
        {
            internal int CallCount { get; private set; }

            internal bool Throw { get; set; }

            public Task<CopilotAdoptionSummary> GetCopilotSummary(CancellationToken cancellationToken)
            {
                CallCount++;
                if (Throw) throw new InvalidOperationException("analysis blew up");
                return Task.FromResult(TenantShapedSummary());
            }

            public Task<LicenceActivityOverview> GetLicenceOverview(CancellationToken cancellationToken)
            {
                return Task.FromResult(TenantShapedOverview());
            }
        }

        #endregion
    }
}

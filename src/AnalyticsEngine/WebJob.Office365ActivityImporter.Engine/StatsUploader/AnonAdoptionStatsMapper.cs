using Common.Entities.CopilotAdoption;
using Common.Entities.LicenceActivity;
using System;
using System.Collections.Generic;
using System.Linq;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// Turns the full Copilot adoption analysis and licence activity overview into the anonymised
    /// aggregates that are safe to upload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is the privacy boundary, and it is deliberately PURE - no database, no clock, no
    /// configuration - so the rules can be tested exhaustively by feeding it tenant-shaped data and
    /// inspecting the serialised output.
    /// </para>
    /// <para>
    /// It works by ALLOW-LIST, never by deny-list. Nothing reaches the output unless this file
    /// explicitly copies it across, so a new field appearing on <see cref="CopilotAdoptionSummary"/>
    /// or <see cref="LicenceActivityOverview"/> is silently ignored rather than silently uploaded.
    /// That matters, because the source models carry a lot that must never leave the tenant:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>AdoptionByDepartment</c>, <c>AdoptionByCountry</c>,
    /// <c>IntensityByDepartment</c>, <c>OpportunityByDepartment</c>, <c>CombinedByDepartment</c>,
    /// <c>CoworkByDepartment</c> - real department and country names.</description></item>
    /// <item><description><c>Agents.MostPopularAgent</c>, <c>Agents.MostVersatileAgent</c>,
    /// <c>Agents.UsageByAgent</c>, <c>Agents.Agents[]</c> - custom agent names, authored by the
    /// customer.</description></item>
    /// <item><description><c>Warnings</c> and <c>IncompleteReasons</c> - built as
    /// <c>"Could not load {x}: {InnermostMessage(ex)}"</c>, so a SQL failure embeds database and
    /// object names. Only the COUNT is reported.</description></item>
    /// <item><description><c>LicenceActivityOverview.Departments</c> / <c>Countries</c> /
    /// <c>Messages</c>, and <c>LicenceActivityCoverage.Message</c> - demographics and free
    /// text.</description></item>
    /// <item><description><c>LicenceActivitySku.Name</c> - falls back to the raw SKU part number for
    /// unrecognised SKUs, so it carries reseller-specific strings while adding nothing.</description></item>
    /// </list>
    /// </remarks>
    public static class AnonAdoptionStatsMapper
    {
        /// <summary>
        /// Maps whichever inputs are available. Either may be null - a deployment can have licence data
        /// without a usable Copilot analysis, and vice versa.
        /// </summary>
        /// <param name="generatedUtc">When the analysis ran.</param>
        /// <param name="windowDays">Reporting window the figures cover.</param>
        public static AnonAdoptionStats Map(
            CopilotAdoptionSummary summary,
            LicenceActivityOverview overview,
            ISkuAllowList skuAllowList,
            DateTime generatedUtc,
            int windowDays)
        {
            var result = new AnonAdoptionStats
            {
                GeneratedUtc = generatedUtc,
                WindowDays = windowDays > 0 ? windowDays : (int?)null,
            };

            if (summary == null)
            {
                result.Suppressed = true;
                result.SuppressionReason = AnonStatsPrivacy.SuppressionReasons.NotAvailable;
            }
            else if (summary.LicensedUsers < AnonStatsPrivacy.MinimumTenantSeats)
            {
                // Too few seats for even a bucketed figure to be anything but a description of a
                // handful of identifiable people. The licence block below is judged separately: a
                // tenant with 5 Copilot seats may still have hundreds of E3 seats worth reporting.
                result.Suppressed = true;
                result.SuppressionReason = AnonStatsPrivacy.SuppressionReasons.BelowMinimumPopulation;
            }
            else
            {
                result.Copilot = MapCopilot(summary);
            }

            result.Licences = MapLicences(overview, skuAllowList);

            return result;
        }

        private static AnonCopilotAdoptionStats MapCopilot(CopilotAdoptionSummary s)
        {
            var sources = s.DataSources ?? new AdoptionDataSources();
            var agents = s.Agents ?? new AgentEstateSummary();

            return new AnonCopilotAdoptionStats
            {
                AuditAvailable = sources.AuditAvailable,
                CopilotUsageReportAvailable = sources.CopilotUsageReportAvailable,
                M365UsageReportsAvailable = sources.M365UsageReportsAvailable,
                UserMetadataAvailable = sources.UserMetadataAvailable,
                CopilotUsageReportObfuscated = sources.CopilotUsageReportObfuscated,

                LicensedUsers = AnonStatsPrivacy.Bucket(s.LicensedUsers),
                ScoredUsers = AnonStatsPrivacy.Bucket(s.ScoredUsers),
                ActiveUsers = AnonStatsPrivacy.Bucket(s.ActiveUsers),
                HabitualUsers = AnonStatsPrivacy.Bucket(s.HabitualUsers),
                NeverUsedUsers = AnonStatsPrivacy.Bucket(s.NeverUsedUsers),
                DormantUsers = AnonStatsPrivacy.Bucket(s.DormantUsers),
                DisabledLicensedUsers = AnonStatsPrivacy.Bucket(s.DisabledLicensedUsers),
                ReclaimableSeats = AnonStatsPrivacy.Bucket(s.ReclaimableSeats),
                UnlicensedActiveUsers = AnonStatsPrivacy.Bucket(s.UnlicensedActiveUsers),
                RecommendedForLicence = AnonStatsPrivacy.Bucket(s.RecommendedForLicence),
                CoworkUsers = AnonStatsPrivacy.Bucket(s.CoworkUsers),

                TotalInteractions = AnonStatsPrivacy.Bucket(s.TotalInteractions),
                CoworkInteractions = AnonStatsPrivacy.Bucket(s.CoworkInteractions),
                AgentInteractions = AnonStatsPrivacy.Bucket(agents.AgentInteractions),

                KnownAgents = AnonStatsPrivacy.Bucket(agents.KnownAgents),
                ActiveAgents = AnonStatsPrivacy.Bucket(agents.ActiveAgents),
                CustomAgents = AnonStatsPrivacy.Bucket(agents.CustomAgents),
                AgentUsers = AnonStatsPrivacy.Bucket(agents.AgentUsers),

                AdoptionRatePct = AnonStatsPrivacy.RoundPct(s.AdoptionRatePct),
                HabitRatePct = AnonStatsPrivacy.RoundPct(s.HabitRatePct),
                CoworkAdoptionPct = AnonStatsPrivacy.RoundPct(s.CoworkAdoptionPct),
                AverageAdoptionScore = AnonStatsPrivacy.RoundScore(s.AverageAdoptionScore),
                MedianAdoptionScore = AnonStatsPrivacy.RoundScore(s.MedianAdoptionScore),

                BandBreakdown = MapCategories(s.BandBreakdown, AnonStatsPrivacy.AdoptionBands),
                Funnel = MapCategories(s.Funnel, AnonStatsPrivacy.FunnelStages),
                HabitBuckets = MapHabitBuckets(s.HabitBuckets),

                FiguresIncomplete = s.FiguresIncomplete,
                UsageReportWindowMismatch = s.UsageReportWindowMismatch,

                // COUNT ONLY - see the class remarks. The strings embed exception text.
                WarningCount = s.Warnings?.Count ?? 0,
            };
        }

        private static List<AnonAdoptionCategory> MapCategories(
            IEnumerable<AdoptionCategory> categories, IReadOnlyCollection<string> allowed)
        {
            var result = new List<AnonAdoptionCategory>();
            if (categories == null) return result;

            foreach (var c in categories)
            {
                if (c == null) continue;

                // Fail closed: a label we do not recognise is dropped, not forwarded. Losing a bar from
                // a chart is a cheap, visible failure; forwarding an unreviewed string is not.
                var label = AnonStatsPrivacy.AllowLabel(c.Label, allowed);
                if (label == null) continue;

                result.Add(new AnonAdoptionCategory
                {
                    Label = label,
                    Value = AnonStatsPrivacy.Bucket(ToCount(c.Value)),
                });
            }

            return result;
        }

        private static List<AnonAdoptionCategory> MapHabitBuckets(IEnumerable<AdoptionHabitBucket> buckets)
        {
            var result = new List<AnonAdoptionCategory>();
            if (buckets == null) return result;

            foreach (var b in buckets)
            {
                if (b == null) continue;

                var label = AnonStatsPrivacy.AllowLabel(b.Label, AnonStatsPrivacy.HabitBuckets);
                if (label == null) continue;

                // RangeLabel is deliberately not sent: it is derived entirely from the options, so it
                // adds nothing the consumer cannot reconstruct.
                result.Add(new AnonAdoptionCategory
                {
                    Label = label,
                    Value = AnonStatsPrivacy.Bucket(b.Users),
                });
            }

            return result;
        }

        private static AnonLicenceAdoptionStats MapLicences(LicenceActivityOverview o, ISkuAllowList skuAllowList)
        {
            if (o == null) return null;

            return new AnonLicenceAdoptionStats
            {
                DistinctAssignedUsers = AnonStatsPrivacy.Bucket(o.DistinctAssignedUsers),
                Skus = MapSkus(o.Licences, skuAllowList),
                Coverage = MapCoverage(o.Coverage),
            };
        }

        /// <summary>
        /// Groups SKUs by their reportable name, then applies the per-SKU population threshold.
        /// </summary>
        /// <remarks>
        /// Raw values are summed BEFORE bucketing. Bucketing first and then adding would compound the
        /// rounding error across every SKU in a group, which on a large tenant is a bigger distortion
        /// than the anonymisation it is meant to provide.
        ///
        /// Note these are licence ASSIGNMENTS, not people: one person holding E3 and a Copilot seat
        /// counts in both rows. That is already true of the report this comes from
        /// (<c>LicenceActivityRules.AssignmentCaveat</c>), and <c>DistinctAssignedUsers</c> is the
        /// figure that does not double count.
        /// </remarks>
        private static List<AnonLicenceSkuStats> MapSkus(
            IEnumerable<LicenceActivitySku> licences, ISkuAllowList skuAllowList)
        {
            var result = new List<AnonLicenceSkuStats>();
            if (licences == null) return result;

            var groups = new Dictionary<string, SkuAccumulator>(StringComparer.Ordinal);

            foreach (var licence in licences)
            {
                if (licence == null) continue;

                // SkuId carries the SKU PART NUMBER here (the licence-activity SQL selects
                // lt.sku_id AS SkuPartNumber), not a GUID.
                var published = skuAllowList != null && skuAllowList.IsPublished(licence.SkuId);
                var name = published ? licence.SkuId : AnonStatsPrivacy.UnlistedSku;

                if (!groups.TryGetValue(name, out var acc))
                {
                    acc = new SkuAccumulator();
                    groups[name] = acc;
                }

                acc.Add(licence);
            }

            var small = new SkuAccumulator();
            var smallCount = 0;

            foreach (var pair in groups.OrderByDescending(g => g.Value.AssignedUsers).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                if (pair.Value.AssignedUsers < AnonStatsPrivacy.MinimumSkuSeats)
                {
                    small.Absorb(pair.Value);
                    smallCount += pair.Value.SkuCount;
                    continue;
                }

                result.Add(pair.Value.ToStats(pair.Key, rolledUpSkuCount: null));
            }

            // The roll-up has to clear the same bar it exists to enforce, otherwise a tenant with three
            // two-seat SKUs would simply be reported as one six-seat row.
            if (small.AssignedUsers >= AnonStatsPrivacy.MinimumSkuSeats)
            {
                result.Add(small.ToStats(AnonStatsPrivacy.OtherSkuBucket, rolledUpSkuCount: smallCount));
            }

            return result;
        }

        private static List<AnonLicenceCoverage> MapCoverage(IEnumerable<LicenceActivityCoverage> coverage)
        {
            var result = new List<AnonLicenceCoverage>();
            if (coverage == null) return result;

            foreach (var c in coverage)
            {
                if (c == null) continue;

                // The workload is the row's identity, so an unrecognised one drops the whole row. The
                // other three degrade to null, which reads as "not reported" rather than losing the
                // coverage signal entirely.
                var workload = AnonStatsPrivacy.AllowLabel(c.Workload, AnonStatsPrivacy.Workloads);
                if (workload == null) continue;

                result.Add(new AnonLicenceCoverage
                {
                    Workload = workload,
                    Status = AnonStatsPrivacy.AllowLabel(c.Status, AnonStatsPrivacy.CoverageStatuses),
                    Source = AnonStatsPrivacy.AllowLabel(c.Source, AnonStatsPrivacy.CoverageSources),
                    Granularity = AnonStatsPrivacy.AllowLabel(c.Granularity, AnonStatsPrivacy.CoverageGranularities),
                    LagDays = c.LagDays,
                    ReportPeriodDays = c.ReportPeriodDays,
                    ExpectedSamples = c.ExpectedSamples,
                    ObservedSamples = c.ObservedSamples,
                    UnmatchedUsers = AnonStatsPrivacy.Bucket(c.UnmatchedUsers),

                    // Message, EffectiveFrom/ToUtc, LatestImportUtc and SnapshotDates are deliberately
                    // dropped: Message is free text, and the dates pin the tenant's import history to
                    // the day.
                });
            }

            return result;
        }

        private static long ToCount(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return 0;
            if (value >= long.MaxValue) return long.MaxValue;
            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        /// <summary>Sums raw SKU figures so bucketing can be applied once, at the end.</summary>
        private sealed class SkuAccumulator
        {
            private readonly Dictionary<string, long[]> _workloads = new Dictionary<string, long[]>(StringComparer.Ordinal);

            internal long AssignedUsers { get; private set; }

            internal int SkuCount { get; private set; }

            internal void Add(LicenceActivitySku licence)
            {
                AssignedUsers += licence.AssignedUsers;
                SkuCount++;

                if (licence.Workloads == null) return;

                foreach (var w in licence.Workloads)
                {
                    if (w == null) continue;

                    var workload = AnonStatsPrivacy.AllowLabel(w.Workload, AnonStatsPrivacy.Workloads);
                    if (workload == null) continue;

                    if (!_workloads.TryGetValue(workload, out var totals))
                    {
                        totals = new long[5];
                        _workloads[workload] = totals;
                    }

                    totals[0] += w.High;
                    totals[1] += w.Moderate;
                    totals[2] += w.Low;
                    totals[3] += w.Zero;
                    totals[4] += w.Unknown;
                }
            }

            internal void Absorb(SkuAccumulator other)
            {
                AssignedUsers += other.AssignedUsers;
                SkuCount += other.SkuCount;

                foreach (var pair in other._workloads)
                {
                    if (!_workloads.TryGetValue(pair.Key, out var totals))
                    {
                        totals = new long[5];
                        _workloads[pair.Key] = totals;
                    }

                    for (var i = 0; i < totals.Length; i++)
                    {
                        totals[i] += pair.Value[i];
                    }
                }
            }

            internal AnonLicenceSkuStats ToStats(string skuPartNumber, int? rolledUpSkuCount)
            {
                return new AnonLicenceSkuStats
                {
                    SkuPartNumber = skuPartNumber,
                    AssignedUsers = AnonStatsPrivacy.Bucket(AssignedUsers),
                    RolledUpSkuCount = rolledUpSkuCount,
                    Workloads = _workloads
                        .OrderBy(w => w.Key, StringComparer.Ordinal)
                        .Select(w => new AnonWorkloadDistribution
                        {
                            Workload = w.Key,
                            High = AnonStatsPrivacy.Bucket(w.Value[0]),
                            Moderate = AnonStatsPrivacy.Bucket(w.Value[1]),
                            Low = AnonStatsPrivacy.Bucket(w.Value[2]),
                            Zero = AnonStatsPrivacy.Bucket(w.Value[3]),
                            Unknown = AnonStatsPrivacy.Bucket(w.Value[4]),
                        })
                        .ToList(),
                };
            }
        }
    }
}

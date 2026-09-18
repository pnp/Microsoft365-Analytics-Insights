using Common.Entities.Copilot;
using Common.Entities.Xlsx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Renders a complete Copilot adoption analysis as an Excel workbook - every figure, table and
    /// chart that is on screen, with the charts live and bound to the cells rather than pasted in as
    /// pictures.
    ///
    /// The purpose is the point-in-time snapshot. A screenshot of a dashboard cannot be compared with
    /// another screenshot six months later: the numbers cannot be subtracted, the definitions may have
    /// moved, and nobody can tell which period either was run over. A workbook can - the run metadata
    /// and every threshold used are written into the Report sheet, so two files taken before and after
    /// an enablement programme are directly comparable and the reader can prove the comparison is fair.
    ///
    /// Built from the same cached analysis the API serves the screen from, so the workbook can never
    /// quietly disagree with the page it was downloaded from.
    /// </summary>
    public static class CopilotAdoptionWorkbook
    {
        /// <summary>Rows of a per-user list written to the workbook before it is truncated.</summary>
        public const int MaxUserRows = 20000;

        /// <summary>Builds the workbook and returns it as a byte array ready to stream to the browser.</summary>
        public static byte[] Build(CopilotAdoptionAnalysis analysis)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            using (var workbook = new XlsxWriter())
            {
                var summary = analysis.Summary;

                WriteReportSheet(workbook, summary);
                WriteHeadlineSheet(workbook, summary);
                WriteMovementSheet(workbook, summary);
                WriteTargetsSheet(workbook, summary);
                WriteFunnelSheet(workbook, summary);
                WriteEngagementSheet(workbook, summary);
                WriteTrendSheet(workbook, summary);
                WriteDepartmentSheet(workbook, summary);
                WriteAgentSheet(workbook, summary);
                WriteUnlicensedSheet(workbook, summary);
                WriteActionPlanSheet(workbook, summary);
                WriteLicensedUsersSheet(workbook, analysis);
                WriteCoworkSheet(workbook, analysis);
                WriteCoworkEstimateSheet(workbook, summary);
                WriteOpportunitiesSheet(workbook, analysis);
                WriteMethodSheet(workbook, summary);

                return workbook.ToArray();
            }
        }

        public static byte[] Build(CopilotAdoptionCohortComparison comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison));

            using (var workbook = new XlsxWriter())
            {
                WriteCohortReportSheet(workbook, comparison);
                WriteCohortTransitionSheet(workbook, comparison);
                WriteCohortActivationSheet(workbook, comparison);
                WriteCohortUsersSheet(workbook, comparison);
                return workbook.ToArray();
            }
        }

        /// <summary>File name carrying the period and the run date, so two snapshots never collide.</summary>
        public static string FileName(CopilotAdoptionSummary summary)
        {
            var generated = summary?.GeneratedUtc ?? DateTime.UtcNow;
            var windowDays = summary?.WindowDays ?? 0;

            return string.Format(
                CultureInfo.InvariantCulture,
                "copilot-adoption-{0}d-{1:yyyy-MM-dd}.xlsx",
                windowDays,
                generated);
        }

        public static string CohortFileName(CopilotAdoptionPeriodComparisonGate gate)
        {
            var left = gate?.Left?.PeriodEnd ?? DateTime.UtcNow.Date;
            var right = gate?.Right?.PeriodEnd ?? DateTime.UtcNow.Date;

            return string.Format(
                CultureInfo.InvariantCulture,
                "copilot-adoption-cohorts-{0:yyyy-MM-dd}-to-{1:yyyy-MM-dd}.xlsx",
                left,
                right);
        }

        private static void WriteCohortReportSheet(XlsxWriter workbook, CopilotAdoptionCohortComparison comparison)
        {
            var sheet = workbook.AddSheet("Report");
            sheet.SetColumnWidths(38, 34, 70);
            sheet.AddTitle("Microsoft 365 Copilot - cohort progression");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Property", "Value", "Notes");
            AddMeta(sheet, "Earlier period end", comparison.Gate.Left?.PeriodEnd, "The closed period the cohort starts from.");
            AddMeta(sheet, "Current period end", comparison.Gate.Right?.PeriodEnd, "The closed period the cohort is measured against.");
            AddMeta(sheet, "Period length", comparison.Gate.Left?.PeriodDays ?? comparison.Gate.Right?.PeriodDays ?? 0, "Both periods must have the same length.");
            AddMeta(sheet, "Options comparable", YesNo(comparison.Gate.OptionsComparable), comparison.Gate.Message);
            AddMeta(sheet, "Earlier population", comparison.Summary.EarlierPopulation, "Every transition except Newly assigned partitions this population.");
            AddMeta(sheet, "Current population", comparison.Summary.CurrentPopulation, "Current period seat holders.");
            AddMeta(sheet, "Integrity check", YesNo(comparison.Summary.TransitionsSumToEarlierPopulation),
                "Retained + Reactivated + Lapsed + Reclaimed + Still at risk must equal the earlier population.");
            AddMeta(sheet, "Activation window", $"{comparison.Activation.ActivationWindowDays} days",
                "Defaults to the reclaim grace-period concept so a seat is not both too new and failed.");
            AddMeta(sheet, "Activation caveat", string.Empty, comparison.Activation.Caveat);
            AddMeta(sheet, "Reclaim caveat", string.Empty, comparison.Summary.ReclaimCaveat);
            sheet.FreezeTopRows(1);
        }

        private static void WriteCohortTransitionSheet(XlsxWriter workbook, CopilotAdoptionCohortComparison comparison)
        {
            var sheet = workbook.AddSheet("Cohort transitions");
            sheet.SetColumnWidths(28, 14, 18, 70, 22, 22, 18);
            sheet.AddTitle("Cohort transitions");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Transition", "Users", "% of earlier", "Definition");
            var first = sheet.CurrentRow + 1;
            foreach (var transition in comparison.Transitions)
            {
                sheet.AddRow(transition.Label, transition.Users, transition.ShareOfEarlierPopulationPct, XlsxCell.Wrapped(transition.Description));
            }
            var last = sheet.CurrentRow;
            if (last >= first)
            {
                var chart = new XlsxChart
                {
                    Type = XlsxChartType.Column,
                    Title = "Cohort transitions",
                    CategoryRange = sheet.RangeReference(first, 1, last, 1),
                    AnchorCell = "F3",
                    ShowDataLabels = true,
                    ShowLegend = false,
                };
                chart.AddSeries("Users", sheet.RangeReference(first, 2, last, 2));
                sheet.AddChart(chart);
            }

            sheet.AddBlankRow();
            sheet.AddHeaderRow("From band", "To band", "Transition", "Users");
            foreach (var flow in comparison.Flows)
            {
                sheet.AddRow(flow.FromBand, flow.ToBand, flow.Transition, flow.Users);
            }
            sheet.FreezeTopRows(3);
        }

        private static void WriteCohortActivationSheet(XlsxWriter workbook, CopilotAdoptionCohortComparison comparison)
        {
            var a = comparison.Activation;
            var sheet = workbook.AddSheet("Activation");
            sheet.SetColumnWidths(36, 16, 18, 24, 24, 18);
            sheet.AddTitle("Time to first use and activation");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Measure", "Value", "Notes");
            AddMeta(sheet, "Known seat-start users", a.KnownSeatStartUsers, "Rows with a real seat_first_observed_utc inside the stored history.");
            AddMeta(sheet, "Seat date unknown", a.SeatDateUnknownUsers, "Excluded from time-to-first-use. Account age is not substituted.");
            AddMeta(sheet, "Assigned before history", a.AssignedBeforeHistoryUsers, "Excluded because first use may predate the retained history.");
            AddMeta(sheet, "New seats assigned in period", a.NewSeatsAssignedInPeriod, "Denominator for activation rate.");
            AddMeta(sheet, $"Activated within {a.ActivationWindowDays} days", a.ActivatedWithinWindow, "Seats that reached first use inside the configured window.");
            AddMeta(sheet, "Activation rate %", a.ActivationRatePct, "New seats activated within the configured window.");
            AddMeta(sheet, "Never activated", a.NeverActivatedUsers, "Known seat date, outside the activation window, and no first use.");
            AddMeta(sheet, "Too new to judge", a.TooNewToJudgeUsers, "Known seat date but still inside the activation window.");
            AddMeta(sheet, "Median days to first use", a.MedianDaysToFirstUse.HasValue ? (object)a.MedianDaysToFirstUse.Value : "-", "Median, not a mean.");

            sheet.AddBlankRow();
            sheet.AddHeaderRow("Distribution", "Users", "% of activated");
            var distFirst = sheet.CurrentRow + 1;
            foreach (var bucket in a.Distribution)
            {
                sheet.AddRow(bucket.Label, bucket.Users, bucket.SharePct);
            }
            var distLast = sheet.CurrentRow;
            if (distLast >= distFirst)
            {
                var chart = new XlsxChart
                {
                    Type = XlsxChartType.Column,
                    Title = "Days to first use",
                    CategoryRange = sheet.RangeReference(distFirst, 1, distLast, 1),
                    AnchorCell = "E4",
                    ShowDataLabels = true,
                    ShowLegend = false,
                };
                chart.AddSeries("Users", sheet.RangeReference(distFirst, 2, distLast, 2));
                sheet.AddChart(chart);
            }

            sheet.AddBlankRow();
            sheet.AddHeaderRow("Department", "New seats", "Activated in window", "Activation rate %", "Never activated", "Seat date unknown");
            foreach (var segment in a.ByDepartment)
            {
                sheet.AddRow(segment.Segment, segment.NewSeatsAssignedInPeriod, segment.ActivatedWithinWindow,
                    segment.ActivationRatePct, segment.NeverActivatedUsers, segment.SeatDateUnknownUsers);
            }
            sheet.FreezeTopRows(3);
        }

        private static void WriteCohortUsersSheet(XlsxWriter workbook, CopilotAdoptionCohortComparison comparison)
        {
            var rows = comparison.Rows ?? new List<CopilotAdoptionCohortUserRow>();
            if (rows.Count == 0) return;

            var sheet = workbook.AddSheet("Cohort users");
            sheet.SetColumnWidths(34, 24, 20, 24, 18, 18, 18, 34, 18, 18, 18, 60);
            sheet.AddTitle(rows.Count > MaxUserRows ? "Cohort users - TRUNCATED" : "Cohort users");
            if (rows.Count > MaxUserRows)
            {
                sheet.AddRow(XlsxCell.Wrapped($"This sheet lists the first {MaxUserRows:N0} of {rows.Count:N0} cohort rows. Use the API drill-through for the full population."));
            }
            sheet.AddBlankRow();
            sheet.AddHeaderRow("User", "Department", "Job title", "Manager", "Transition", "From band", "To band",
                "Reclaim interpretation", "Seat first observed", "First use", "Days to first use", "Activation state");
            var headerRow = sheet.CurrentRow;
            foreach (var row in rows.Take(MaxUserRows))
            {
                sheet.AddRow(row.UserPrincipalName, row.Department, row.JobTitle, row.ManagerUserPrincipalName,
                    row.TransitionLabel, row.FromBand, row.ToBand, XlsxCell.Wrapped(row.ReclaimInterpretation),
                    row.SeatFirstObservedUtc, row.FirstInteractionUtc, row.DaysToFirstUse, row.ActivationState);
            }
            sheet.FreezeTopRows(headerRow);
            sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 12);
        }

        #region Report metadata

        /// <summary>
        /// The cover sheet: what this file is, what it covers, and every threshold used to produce it.
        ///
        /// The thresholds are the important part. Two snapshots are only comparable if they were
        /// scored by the same rules, and this is what lets a reader confirm that rather than assume
        /// it - the tuning is adjustable, so "adoption went up" could otherwise mean "the bar moved".
        /// </summary>
        private static void WriteReportSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)        {
            var sheet = workbook.AddSheet("Report");
            sheet.SetColumnWidths(42, 34, 60);

            sheet.AddTitle("Microsoft 365 Copilot - adoption report");
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Property", "Value", "Notes");
            AddMeta(sheet, "Generated (UTC)", summary.GeneratedUtc,
                "Take a snapshot before an enablement programme and another afterwards; the two files are directly comparable.");
            AddMeta(sheet, "Period covered", $"{summary.WindowDays} days",
                "All 'this period' figures use this window.");
            AddMeta(sheet, "Users analysed", summary.ScoredUsers,
                summary.ScoredUsers < summary.LicensedUsers
                    ? $"Of {summary.LicensedUsers:N0} licences. Rates in this workbook are of the analysed users only."
                    : "Every licensed user was analysed.");
            AddMeta(sheet, "From (UTC)", summary.FromUtc, string.Empty);
            AddMeta(sheet, "To (UTC)", summary.ToUtc, string.Empty);
            AddMeta(sheet, "History window", $"{summary.Options.HistoryDays} days",
                "How far back 'ever used Copilot' looks, which is what separates Dormant from Never used.");
            AddMeta(sheet, "Microsoft guidance catalogue", summary.GuidanceCatalogueVersion,
                "Version of the Microsoft-published guidance links attached to recommended actions in this workbook.");
            AddMeta(sheet, "Figures incomplete", YesNo(summary.FiguresIncomplete),
                summary.FiguresIncomplete
                    ? "A source query failed or timed out. Treat every individual row and aggregate in this workbook as incomplete."
                    : "No headline-source query reported an incomplete result.");
            AddMeta(sheet, "Individual rows", summary.Options == null ? string.Empty : "See per-user sheets",
                "The Licensed users and Licence opportunities sheets contain individual-level governance data. Do not share externally without a legal basis.");

            sheet.AddBlankRow();
            sheet.AddHeaderRow("Data source", "Available", "Notes");
            AddMeta(sheet, "Copilot audit log", YesNo(summary.DataSources.AuditAvailable),
                "Covers every user including unlicensed Copilot Chat, and matches the period exactly.");
            AddMeta(sheet, "Microsoft Copilot usage report", YesNo(summary.DataSources.CopilotUsageReportAvailable),
                summary.DataSources.CopilotUsageReportDate.HasValue
                    ? $"Snapshot of {summary.DataSources.CopilotUsageReportDate.Value:yyyy-MM-dd}. Licensed users only."
                    : "Not imported.");
            AddMeta(sheet, "Microsoft 365 usage reports", YesNo(summary.DataSources.M365UsageReportsAvailable),
                summary.DataSources.M365UsageReportDate.HasValue
                    ? $"Daily reports read across the whole period, up to {summary.DataSources.M365UsageReportDate.Value:yyyy-MM-dd}. "
                      + "Per-user figures are an average across the days that user was active."
                    : "Not imported.");
            AddMeta(sheet, "User metadata", YesNo(summary.DataSources.UserMetadataAvailable),
                "Supplies the licensed population and every department breakdown.");
            AddMeta(sheet, "Tenant conceals user information", YesNo(summary.DataSources.CopilotUsageReportObfuscated),
                "When true, Microsoft's per-user report is unusable; audit-derived figures are unaffected.");

            sheet.AddBlankRow();
            sheet.AddHeaderRow("Threshold used", "Value", "What it controls");
            var o = summary.Options;
            AddMeta(sheet, "Frequency weight", o.FrequencyWeight, "Share of the engagement score from days used.");
            AddMeta(sheet, "Depth weight", o.DepthWeight, "Share from interactions per active day.");
            AddMeta(sheet, "Breadth weight", o.BreadthWeight, "Share from number of Copilot surfaces used.");
            AddMeta(sheet, "Frequency target", o.FrequencyTargetRatio, "Share of working days needed for full marks.");
            AddMeta(sheet, "Depth target", o.DepthTargetInteractionsPerActiveDay, "Interactions per active day for full marks.");
            AddMeta(sheet, "Depth minimum active days", o.DepthMinActiveDays, "Below this many active days the depth component is scaled down in proportion, so one busy afternoon cannot read as a habit.");
            AddMeta(sheet, "Breadth target", o.BreadthTargetApps, "Copilot surfaces for full marks.");
            AddMeta(sheet, "Champion at", o.ChampionScore, "Engagement score for the Champion band.");
            AddMeta(sheet, "Established at", o.EstablishedScore, "The 'habit formed' line - what 'habitual users' counts.");
            AddMeta(sheet, "Developing at", o.DevelopingScore, "Engagement score for the Developing band.");
            AddMeta(sheet, "Habit month length", o.HabitBucketNormalisationDays, "Active days are restated per this many days.");
            AddMeta(sheet, "Licence recommendation at", o.OpportunityRecommendScore, "Business-case score for a recommended candidate.");
            AddMeta(sheet, "Agent review after", $"{o.AgentReviewInactiveDays} days", "Inactivity before an agent is reviewed.");
            AddMeta(sheet, "Agent retire after", $"{o.AgentRetireInactiveDays} days", "Inactivity before an agent is proposed for retirement.");
            AddMeta(sheet, "Agent minimum users", o.AgentMinUsers, "Users an agent needs before its use counts as adoption.");
            AddMeta(sheet, "New-seat activation window", $"{o.ActivationWindowDays} days", "Days from first observed seat assignment to first use for activation-rate reporting.");

            if (summary.Warnings.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Warnings affecting these figures", string.Empty, string.Empty);
                foreach (var warning in summary.Warnings)
                {
                    sheet.AddRow(XlsxCell.Wrapped(warning));
                }
            }

            sheet.FreezeTopRows(1);
        }


        private static void WriteMovementSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var sheet = workbook.AddSheet("Period movement");
            sheet.SetColumnWidths(30, 18, 18, 18, 18, 18, 70);
            sheet.AddTitle("Closed-period movement");
            sheet.AddBlankRow();
            var movement = summary.PeriodMovement;
            if (movement == null || !movement.Available || !movement.Comparable)
            {
                sheet.AddHeaderRow("Status", "Message");
                sheet.AddRow(movement == null ? "Not available" : "Not comparable", XlsxCell.Wrapped(movement?.Message ?? "No movement was calculated."));
                return;
            }

            sheet.AddHeaderRow("Measure", "Current", "Prior", "Change", "Current seats", "Seat change", "Comparison");
            foreach (var delta in movement.Deltas)
            {
                sheet.AddRow(
                    delta.Label,
                    delta.CurrentValue,
                    delta.PriorValue,
                    delta.Change,
                    delta.DenominatorCurrent.HasValue ? (object)delta.DenominatorCurrent.Value : string.Empty,
                    delta.DenominatorChange.HasValue ? (object)delta.DenominatorChange.Value : string.Empty,
                    XlsxCell.Wrapped(movement.Message));
            }
            sheet.FreezeTopRows(3);
        }

        private static void WriteTargetsSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var sheet = workbook.AddSheet("Targets");
            sheet.SetColumnWidths(28, 16, 28, 18, 18, 18, 18, 24, 70);
            sheet.AddTitle("Customer-defined adoption targets");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Metric", "Scope", "Owner", "Baseline", "Current", "Target", "Progress %", "Target date", "Status");
            foreach (var target in summary.Targets ?? new List<CopilotAdoptionTarget>())
            {
                sheet.AddRow(
                    target.Label ?? target.Metric,
                    target.ScopeType == "tenant" ? "Tenant" : $"{target.ScopeType}: {target.ScopeValue}",
                    target.Owner,
                    target.BaselineValue,
                    target.CurrentValue.HasValue ? (object)target.CurrentValue.Value : string.Empty,
                    target.TargetValue,
                    target.ProgressPct.HasValue ? (object)target.ProgressPct.Value : string.Empty,
                    target.TargetDate,
                    XlsxCell.Wrapped(target.Message));
            }
            if ((summary.Targets ?? new List<CopilotAdoptionTarget>()).Count == 0)
            {
                sheet.AddRow("No targets", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "Create internal targets in the portal; no external benchmark is built in.");
            }
            sheet.FreezeTopRows(3);
        }

        private static void AddMeta(XlsxSheet sheet, string name, object value, string notes)
        {
            sheet.AddRow(name, value, XlsxCell.Wrapped(notes));
        }

        private static string YesNo(bool value)
        {
            return value ? "Yes" : "No";
        }

        private static string WarningSummary(CopilotAdoptionSummary summary)
        {
            if (summary == null) return string.Empty;

            var warnings = new List<string>();
            if (summary.FiguresIncomplete)
            {
                warnings.Add("Figures incomplete: " + string.Join(", ", summary.IncompleteReasons));
            }

            warnings.AddRange(summary.Warnings ?? new List<string>());
            return string.Join(" | ", warnings);
        }

        #endregion

        #region Headline figures

        private static void WriteHeadlineSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var sheet = workbook.AddSheet("Headline figures");
            sheet.SetColumnWidths(38, 16, 62);

            sheet.AddTitle("Headline figures");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Measure", "Value", "What it means");

            var first = sheet.CurrentRow + 1;

            AddMeta(sheet, "Copilot licences", summary.LicensedUsers,
                "Users holding at least one licence classified as a Microsoft 365 Copilot licence. The assigned-seat count.");
            AddMeta(sheet, "Purchased Copilot seats", summary.PurchasedCopilotSeats.HasValue ? (object)summary.PurchasedCopilotSeats.Value : "Unknown",
                "Purchased seats from Graph subscribedSkus prepaidUnits for the SKUs classified as Copilot seats. Unknown means subscribedSkus was unavailable or the permission is missing - deliberately not zero.");
            AddMeta(sheet, "Unassigned Copilot seats", summary.UnassignedCopilotSeats.HasValue ? (object)summary.UnassignedCopilotSeats.Value : "Unknown",
                "Purchased minus assigned, per Copilot SKU. A seat nobody holds, as distinct from a seat somebody holds but does not use - the two need different decisions, so they are never merged.");
            AddMeta(sheet, "Users analysed", summary.ScoredUsers,
                summary.ScoredUsers < summary.LicensedUsers
                    ? "FEWER THAN THE SEAT COUNT. Every rate below is of these users, not of the whole tenant, "
                      + "and must not be quoted as a tenant-wide figure."
                    : "Every licensed user was analysed, so the rates below are tenant-wide.");
            AddMeta(sheet, "Active this period", summary.ActiveUsers,
                "Used Copilot at least once. A deliberately low bar - one interaction counts the same as fifty.");
            AddMeta(sheet, "Habitual users", summary.HabitualUsers,
                $"Engagement score of {summary.Options.EstablishedScore} or more - Copilot is part of the working week. This is the figure that tracks realised value.");
            AddMeta(sheet, "Dormant", summary.DormantUsers,
                "Used Copilot before this period but not inside it. Needs a conversation about what stopped.");
            AddMeta(sheet, "Never used", summary.NeverUsedUsers,
                $"No Copilot activity anywhere in the last {summary.Options.HistoryDays} days. Needs onboarding, or the licence back.");
            AddMeta(sheet, "Disabled accounts with licences", summary.DisabledLicensedUsers,
                "The raw inventory of disabled accounts still holding a Copilot seat, including any an admin has excluded from reclaim. The actionable subset is the certain tier below.");
            AddMeta(sheet, "Reclaimable licences", summary.ReclaimableSeats,
                summary.UsageReportWindowMismatch
                    ? "Certain plus probable reclaim, minus PROBABLE rows scored from Microsoft's usage report because its pinned period does not match this analysis window. Certain (disabled) seats are never held back that way - a disabled account is not an inference from an absence of use. Excludes admin exclusions and review-only cases; leave, part-time patterns, service/shared accounts and role-based mailboxes are not detectable from usage data."
                    : "Certain plus probable reclaim only. Excludes admin exclusions and review-only cases; leave, part-time patterns, service/shared accounts and role-based mailboxes are not detectable from usage data.");
            AddMeta(sheet, "Reclaim - certain", summary.ReclaimCertainSeats,
                "Disabled accounts still holding seats. Act immediately unless there is a known exception.");
            AddMeta(sheet, "Reclaim - probable", summary.ReclaimProbableSeats,
                $"No observed use, account enabled, and the account-age tenure proxy is beyond the {summary.Options.ReclaimGraceDays}-day grace period.");
            AddMeta(sheet, "Reclaim - review", summary.ReclaimReviewSeats,
                "Dormant, too new, or missing enough account state/tenure context to judge automatically.");
            AddMeta(sheet, "Reclaim exclusions", summary.ReclaimExcludedUsers,
                "Reviewed false positives removed from reclaim counts but still included in the licensed denominator.");
            AddMeta(sheet, "Expired exclusions", summary.ExpiredReclaimExclusions,
                "Previously excluded seats whose review-after date has passed and should be looked at again.");
            // The two hold-backs and the disabled-but-active term, so the reader can add the reclaim
            // figures up against the band breakdown and land exactly on it.
            AddMeta(sheet, "Held back - review or exclusion", summary.ReclaimSeatsHeldBackForReview,
                "Never-used or dormant seats kept out of the reclaimable total because a human has to look at them first, or because an admin has already excluded them.");
            AddMeta(sheet, "Held back - report window mismatch", summary.ReclaimSeatsHeldBackForWindowMismatch,
                "Seats kept out of the reclaimable total because they were scored from Microsoft's usage report over a period that is not this analysis window.");
            AddMeta(sheet, "Reclaimable but still active", summary.ReclaimSeatsFromActiveBands,
                "Reclaimable seats that are not never-used or dormant - disabled accounts that were still active when they were disabled. Never used + Dormant + this = Reclaimable + both held-back figures.");

            var last = sheet.CurrentRow;

            sheet.AddBlankRow();
            AddMeta(sheet, "Adoption rate %", summary.AdoptionRatePct, "Active users as a share of the users analysed.");
            AddMeta(sheet, "Habit rate %", summary.HabitRatePct, "Habitual users as a share of the users analysed.");
            AddMeta(sheet, "Average engagement", summary.AverageAdoptionScore, "Mean score out of 100, including licences scoring zero.");
            AddMeta(sheet, "Median engagement", summary.MedianAdoptionScore,
                "Reported next to the mean because a few Champions pull the mean up; a large gap means a long tail of light users.");
            AddMeta(sheet, "Total interactions", summary.TotalInteractions, "Audit-log Copilot interactions by licensed users in the period; Microsoft usage-report prompt counts are not mixed into this total.");
            AddMeta(sheet, "Users scored from Microsoft report", summary.UsageReportSourcedUsers,
                "Licensed users whose score used Microsoft's per-user report because the audit import had no per-user signal for them.");

            sheet.AddBlankRow();
            AddMeta(sheet, "Using Copilot unlicensed", summary.UnlicensedActiveUsers,
                "People with no licence who used Copilot anyway - proven, unmet demand, and invisible in Microsoft's own reports.");
            AddMeta(sheet, "Recommended for a licence", summary.RecommendedForLicence,
                $"Unlicensed users recommended for a licence: either {summary.Options.OpportunityProvenDemandMinActiveDays} or more distinct days of unlicensed Copilot use (proven demand), or a business-case score of {summary.Options.OpportunityRecommendScore} or above (workload inferred).");

            if (summary.CoworkDetected)
            {
                AddMeta(sheet, "Cowork users", summary.CoworkUsers, "Users who used Microsoft 365 Copilot Cowork, preferring Microsoft's Cowork usage-report task source when present.");
                AddMeta(sheet, "Cowork adoption %", summary.CoworkAdoptionPct, "Null when spending-policy eligibility is unknown; never divided by all licensed users.");
                AddMeta(sheet, "Cowork tasks", summary.CoworkReportTotalTasks, "Microsoft's Cowork usage-report task count. Not comparable with audit interactions.");
                AddMeta(sheet, "Cowork audit interactions", summary.CoworkInteractions, "Audit-derived Cowork interactions retained only for reconciliation, not as task counts.");
            }

            if (summary.CoworkReadinessAvailable)
            {
                AddMeta(sheet, "Cowork established users", summary.CoworkEstablishedUsers,
                    $"Using Cowork on at least {summary.Options.CoworkRegularMinActiveDays} separate days - a habit, not a trial.");
                AddMeta(sheet, "Cowork prime candidates", summary.CoworkPrimeCandidates,
                    "PREDICTED, not measured. Copilot-fluent seat holders with a heavy coordination load who are not "
                    + "yet using Cowork. The population a rollout should target first.");
                AddMeta(sheet, "To add to the Cowork policy", summary.CoworkRecommendedForPolicy,
                    "Prime candidates plus everyone already using Cowork. Cowork access is granted by a spending "
                    + "policy scoped to users, and existing users must stay in scope or they lose access.");
            }

            var chart = new XlsxChart
            {
                Type = XlsxChartType.Bar,
                Title = "Licensed population",
                CategoryRange = sheet.RangeReference(first, 1, last, 1),
                AnchorCell = "E3",
                ShowDataLabels = true,
                ShowLegend = false,
            };
            chart.AddSeries("Users", sheet.RangeReference(first, 2, last, 2));
            sheet.AddChart(chart);

            sheet.FreezeTopRows(3);
        }

        #endregion

        #region Funnel, engagement and habits

        private static void WriteFunnelSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            if (summary.Funnel.Count == 0) return;

            var sheet = workbook.AddSheet("Adoption funnel");
            sheet.SetColumnWidths(28, 14, 18, 18);

            sheet.AddTitle("Adoption funnel");
            sheet.AddRow(XlsxCell.Wrapped(
                "Every stage is a subset of the one above it. 'Conversion' is from the stage immediately above, "
                + "not from the top - the biggest single drop is where the effort should go."));
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Stage", "Users", "% of licensed", "Conversion from previous %");

            var first = sheet.CurrentRow + 1;
            var top = summary.Funnel.Count > 0 ? summary.Funnel[0].Value : 0;

            for (var i = 0; i < summary.Funnel.Count; i++)
            {
                var stage = summary.Funnel[i];
                var previous = i == 0 ? (double?)null : summary.Funnel[i - 1].Value;

                sheet.AddRow(
                    stage.Label,
                    stage.Value,
                    Percentage(stage.Value, top),
                    previous.HasValue ? (object)Percentage(stage.Value, previous.Value) : "baseline");
            }

            var last = sheet.CurrentRow;

            var chart = new XlsxChart
            {
                Type = XlsxChartType.Bar,
                Title = "Adoption funnel",
                CategoryRange = sheet.RangeReference(first, 1, last, 1),
                AnchorCell = "F3",
                ShowDataLabels = true,
                ShowLegend = false,
            };
            chart.AddSeries("Users", sheet.RangeReference(first, 2, last, 2));
            sheet.AddChart(chart);
        }

        private static void WriteEngagementSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var sheet = workbook.AddSheet("Engagement");
            sheet.SetColumnWidths(26, 14, 16, 34);

            sheet.AddTitle("Engagement bands and usage frequency");
            sheet.AddBlankRow();

            // --- Bands -----------------------------------------------------------------------
            // Bands partition the SCORED population, so that is their denominator - not the seat count.
            // Dividing by LicensedUsers made the column sum to less than 100% on a capped tenant and
            // contradicted both the funnel two sheets earlier and the donut on screen.
            sheet.AddHeaderRow("Engagement band", "Users", "% of analysed", string.Empty);
            var bandFirst = sheet.CurrentRow + 1;
            foreach (var band in summary.BandBreakdown)
            {
                sheet.AddRow(band.Label, band.Value, Percentage(band.Value, summary.ScoredUsers), string.Empty);
            }
            var bandLast = sheet.CurrentRow;

            var bandChart = new XlsxChart
            {
                Type = XlsxChartType.Doughnut,
                Title = "Engagement mix",
                CategoryRange = sheet.RangeReference(bandFirst, 1, bandLast, 1),
                AnchorCell = "F3",
                ShowLegend = true,
            };
            bandChart.AddSeries("Users", sheet.RangeReference(bandFirst, 2, bandLast, 2));
            sheet.AddChart(bandChart);

            // --- Unweighted usage frequency ---------------------------------------------------
            if (summary.HabitBuckets.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "How often people open Copilot. This is UNWEIGHTED - distinct active days only - and is "
                    + "deliberately a different measure from 'habitual users' above, which is the weighted "
                    + "engagement score. Read the two together: a large Daily figure with a low habit rate means "
                    + "people open Copilot constantly and do very little with it. Counts ACTIVE users only, and "
                    + "restates active days per "
                    + summary.Options.HabitBucketNormalisationDays
                    + "-day month so they mean the same thing whichever period was selected. A licence that was never "
                    + "used is not 'infrequent' - it is an idle seat, assessed by the reclaim confidence tiers."));
                sheet.AddHeaderRow("Usage frequency", "Users", "% of active", "Range");

                var habitFirst = sheet.CurrentRow + 1;
                foreach (var bucket in summary.HabitBuckets)
                {
                    sheet.AddRow(bucket.Label, bucket.Users, bucket.SharePct, bucket.RangeLabel);
                }
                var habitLast = sheet.CurrentRow;

                var habitChart = new XlsxChart
                {
                    Type = XlsxChartType.Column,
                    Title = "How often people open Copilot",
                    CategoryRange = sheet.RangeReference(habitFirst, 1, habitLast, 1),
                    AnchorCell = "F20",
                    ShowDataLabels = true,
                    ShowLegend = false,
                };
                habitChart.AddSeries("Users", sheet.RangeReference(habitFirst, 2, habitLast, 2));
                sheet.AddChart(habitChart);
            }

            // --- Score profile ---------------------------------------------------------------
            if (summary.ScoreProfiles.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "The shape of engagement. Two populations can average the same score with completely "
                    + "different shapes, and the gap between the typical user and the Champions says which of "
                    + "the three behaviours a programme should actually target."));
                sheet.AddHeaderRow("Population", "Frequency", "Depth", "Breadth");
                foreach (var profile in summary.ScoreProfiles)
                {
                    sheet.AddRow(
                        $"{profile.Label} ({profile.Users})",
                        profile.FrequencyScore,
                        profile.DepthScore,
                        profile.BreadthScore);
                }
            }

            // --- Concentration ---------------------------------------------------------------
            if (summary.Concentration.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "Usage concentration. '40% adoption spread evenly' and '40% adoption where a tenth of them "
                    + "do most of it' are the same percentage and completely different situations - the second "
                    + "collapses when those people change team."));
                sheet.AddHeaderRow("Cohort", "Users", "Share of all activity %", "Interactions per user");

                var concFirst = sheet.CurrentRow + 1;
                foreach (var band in summary.Concentration)
                {
                    sheet.AddRow(band.Label, band.Users, band.SharePct, band.InteractionsPerUser);
                }
                var concLast = sheet.CurrentRow;

                var concChart = new XlsxChart
                {
                    Type = XlsxChartType.Pie,
                    Title = "Share of all Copilot activity",
                    CategoryRange = sheet.RangeReference(concFirst, 1, concLast, 1),
                    AnchorCell = "F37",
                    ShowLegend = true,
                };
                concChart.AddSeries("Share %", sheet.RangeReference(concFirst, 3, concLast, 3));
                sheet.AddChart(concChart);
            }
        }

        #endregion

        #region Trend

        private static void WriteTrendSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var allSeries = summary.WeeklyTrend.Concat(summary.WeeklyVolumeTrend).ToList();
            if (allSeries.Count == 0) return;

            var sheet = workbook.AddSheet("Weekly trend");

            var weeks = allSeries
                .SelectMany(s => s.Points.Select(p => p.WeekStart))
                .Distinct()
                .OrderBy(w => w)
                .ToList();

            if (weeks.Count == 0) return;

            var widths = new List<double> { 14 };
            widths.AddRange(allSeries.Select(_ => 22d));
            sheet.SetColumnWidths(widths.ToArray());

            sheet.AddTitle("Weekly trend");
            sheet.AddRow(XlsxCell.Wrapped(
                "Six months of history regardless of the reporting period, because a trend is the one thing the "
                + "period cannot show. Weeks start on a Monday, in UTC, and the current partial week is excluded. "
                + "Missing weeks are written as zero only when Audit.General coverage is verified; otherwise the "
                + "cell is blank. Licence membership is evaluated as of today until closed-period seat snapshots land."));
            sheet.AddBlankRow();

            var headers = new List<string> { "Week starting" };
            headers.AddRange(allSeries.Select(s => s.Name));
            sheet.AddHeaderRow(headers.ToArray());

            var first = sheet.CurrentRow + 1;

            foreach (var week in weeks)
            {
                var row = new List<object> { XlsxCell.Date(week) };
                foreach (var series in allSeries)
                {
                    var point = series.Points.FirstOrDefault(p => p.WeekStart == week);
                    row.Add(point == null ? 0d : point.Value.HasValue ? (object)point.Value.Value : null);
                }
                sheet.AddRow(row.ToArray());
            }

            var last = sheet.CurrentRow;
            var headerRow = first - 1;

            // People and interaction volumes are charted separately: a few hundred users plotted against
            // tens of thousands of interactions flattens the user line onto zero.
            AddTrendChart(sheet, summary.WeeklyTrend, allSeries, first, last, headerRow,
                "Weekly active users", "N3", XlsxChartType.Line);

            var volumeHasGaps = summary.WeeklyVolumeTrend.Any(s => s.Points.Any(p => !p.Value.HasValue));
            AddTrendChart(sheet, summary.WeeklyVolumeTrend, allSeries, first, last, headerRow,
                "Weekly Copilot volume", "N22", volumeHasGaps ? XlsxChartType.Line : XlsxChartType.StackedArea);

            sheet.FreezeTopRows(4);
        }

        private static void AddTrendChart(
            XlsxSheet sheet,
            IEnumerable<AdoptionSeries> wanted,
            IReadOnlyList<AdoptionSeries> allSeries,
            int firstRow,
            int lastRow,
            int headerRow,
            string title,
            string anchor,
            XlsxChartType type)
        {
            var chart = new XlsxChart
            {
                Type = type,
                Title = title,
                CategoryRange = sheet.RangeReference(firstRow, 1, lastRow, 1),
                AnchorCell = anchor,
                WidthCells = 11,
                HeightCells = 18,
                ShowLegend = true,
            };

            foreach (var series in wanted)
            {
                var column = allSeries.ToList().FindIndex(s => s.Name == series.Name) + 2;
                if (column < 2) continue;

                chart.Series.Add(new XlsxChartSeries
                {
                    Name = series.Name,
                    NameRange = sheet.RangeReference(headerRow, column, headerRow, column),
                    ValueRange = sheet.RangeReference(firstRow, column, lastRow, column),
                });
            }

            if (chart.Series.Count > 0) sheet.AddChart(chart);
        }

        #endregion

        #region Departments

        private static void WriteDepartmentSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            // Every section this sheet can render has to be in the guard, or a tenant whose departments
            // are all below the minimum seat count - but which has country or resource data - loses that
            // data entirely and silently.
            if (summary.AdoptionByDepartment.Count == 0
                && summary.CombinedByDepartment.Count == 0
                && summary.IntensityByDepartment.Count == 0
                && summary.AdoptionByCountry.Count == 0
                && summary.AccountabilityRollup.Count == 0
                && summary.UsageByApp.Count == 0
                && summary.TopResourceTypes.Count == 0)
            {
                return;
            }

            var sheet = workbook.AddSheet("Departments and apps");
            sheet.SetColumnWidths(30, 12, 12, 12, 14, 16, 14, 18, 18);

            sheet.AddTitle("Where Copilot is used");
            sheet.AddBlankRow();

            if (summary.AdoptionByDepartment.Count > 0)
            {
                sheet.AddRow(XlsxCell.Wrapped(
                    "Adoption by department, worst first - the running order for an enablement plan. "
                    + $"Departments with fewer than {summary.Options.MinSeatsPerSegment} licences are omitted because "
                    + "the percentage would not be meaningful."));
                sheet.AddHeaderRow("Department", "Seats", "Active", "Habitual", "Never used", "Adoption rate %", "Avg. score");

                var first = sheet.CurrentRow + 1;
                foreach (var row in summary.AdoptionByDepartment)
                {
                    sheet.AddRow(row.Segment, row.LicensedUsers, row.ActiveUsers, row.HabitualUsers,
                        row.NeverUsedUsers, row.AdoptionRatePct, row.AverageAdoptionScore);
                }
                var last = sheet.CurrentRow;

                var chart = new XlsxChart
                {
                    Type = XlsxChartType.Bar,
                    Title = "Adoption rate by department",
                    CategoryRange = sheet.RangeReference(first, 1, last, 1),
                    AnchorCell = "K3",
                    ShowDataLabels = true,
                    ShowLegend = false,
                };
                chart.AddSeries("Adoption rate %", sheet.RangeReference(first, 6, last, 6));
                sheet.AddChart(chart);
            }

            if (summary.IntensityByDepartment.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "Frequency against intensity. Two departments on the same adoption rate can sit in opposite "
                    + "corners here: frequent-but-shallow needs richer scenarios, deep-but-occasional needs a "
                    + "reason to come back tomorrow. Only active users are averaged."));
                sheet.AddHeaderRow("Department", "Seats", "Active users", "Active days/month",
                    "Interactions per active day", "Avg. score (active)");

                foreach (var point in summary.IntensityByDepartment)
                {
                    sheet.AddRow(point.Segment, point.LicensedUsers, point.ActiveUsers,
                        point.ActiveDaysPerUser, point.ActionsPerActiveDay, point.ActiveUserAverageScore);
                }
            }

            if (summary.CombinedByDepartment.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "Licensed and unlicensed side by side. A department with idle licences AND heavy unlicensed use "
                    + "is a licence-allocation problem, not an adoption problem, and can usually be fixed at no cost. "
                    + "Interactions per licence divides by all licences including idle ones - that is the point of the "
                    + "comparison. Both per-user columns are normalised to a month."));
                sheet.AddHeaderRow("Department", "Seats", "Active licences", "Interactions per licence",
                    "Licences using agents %", "Unlicensed users", "Interactions per unlicensed user",
                    "Unlicensed using agents %");

                foreach (var row in summary.CombinedByDepartment)
                {
                    sheet.AddRow(row.Segment, row.LicensedUsers, row.LicensedActiveUsers,
                        row.InteractionsPerLicensedUser, row.LicensedAgentUserPct, row.UnlicensedActiveUsers,
                        row.InteractionsPerUnlicensedUser, row.UnlicensedAgentUserPct);
                }
            }

            if (summary.AccountabilityRollup.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    $"Accountability roll-up by {summary.AccountabilityDimensionLabel ?? "direct manager"}, sorted by the "
                    + "largest absolute opportunity first. This is aggregate-only: it applies the same minimum-seat "
                    + $"suppression ({summary.Options.MinSeatsPerSegment}) as the segment charts and does not add a new "
                    + "named per-user leader view."));
                sheet.AddHeaderRow(
                    summary.AccountabilityDimensionLabel ?? "Accountability unit",
                    "Seats", "Active", "Habitual", "Never used", "Adoption rate %",
                    "Reclaimable", "Certain reclaim", "Probable reclaim", "Review reclaim",
                    "Reclaim/onboard", "Win back", "Coach", "Broaden", "Deepen", "No action",
                    "Advocate", "Review", "Excluded", "Action opportunity");

                foreach (var row in summary.AccountabilityRollup)
                {
                    sheet.AddRow(
                        row.Segment, row.LicensedUsers, row.ActiveUsers, row.HabitualUsers,
                        row.NeverUsedUsers, row.AdoptionRatePct, row.ReclaimableSeats,
                        row.ReclaimCertainSeats, row.ReclaimProbableSeats, row.ReclaimReviewSeats,
                        row.ReclaimUsers, row.ReengageUsers, row.CoachUsers, row.BroadenUsers,
                        row.GrowUsers, row.SustainUsers, row.AdvocateUsers, row.ReviewUsers,
                        row.ExcludedUsers, row.OpportunityUsers);
                }
            }

            if (summary.AdoptionByCountry.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped("The same measures by country, for organisations that run enablement regionally."));
                sheet.AddHeaderRow("Country", "Seats", "Active", "Habitual", "Never used", "Adoption rate %", "Avg. score");
                foreach (var row in summary.AdoptionByCountry)
                {
                    sheet.AddRow(row.Segment, row.LicensedUsers, row.ActiveUsers, row.HabitualUsers,
                        row.NeverUsedUsers, row.AdoptionRatePct, row.AverageAdoptionScore);
                }
            }

            if (summary.UsageByApp.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "Interactions by app across licensed users. Counts interactions rather than people, so one "
                    + "very heavy user can dominate a surface."));
                sheet.AddHeaderRow("Copilot surface", "Interactions");

                var first = sheet.CurrentRow + 1;
                foreach (var app in summary.UsageByApp)
                {
                    sheet.AddRow(app.Label, app.Value);
                }
                var last = sheet.CurrentRow;

                var chart = new XlsxChart
                {
                    Type = XlsxChartType.Column,
                    Title = "Copilot use by app",
                    CategoryRange = sheet.RangeReference(first, 1, last, 1),
                    AnchorCell = "K22",
                    ShowLegend = false,
                };
                chart.AddSeries("Interactions", sheet.RangeReference(first, 2, last, 2));
                sheet.AddChart(chart);
            }

            if (summary.TopResourceTypes.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "What Copilot referenced. These are Microsoft's own AccessedResources.Type values from the "
                    + "audit log, which are not one taxonomy: the Kind column says whether a value names a kind "
                    + "of organisational content, describes how the resource was used, or marks grounding from "
                    + "outside the tenant. A file Copilot cited is typed CITATION rather than by its file type, "
                    + "so the file-type rows undercount real file references."));
                sheet.AddHeaderRow("Resource type", "Kind", "References");
                foreach (var type in summary.TopResourceTypes)
                {
                    sheet.AddRow(type.Label, CopilotAccessedResourceTaxonomy.KindLabel(type.Kind), type.Value);
                }
            }
        }

        #endregion

        #region Agents and unlicensed

        private static void WriteAgentSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var estate = summary.Agents;
            if (estate == null || estate.KnownAgents == 0) return;

            var sheet = workbook.AddSheet("Agents");
            sheet.SetColumnWidths(34, 12, 12, 14, 16, 12, 14, 14, 14, 14, 46);

            sheet.AddTitle("Copilot agent estate");
            sheet.AddRow(XlsxCell.Wrapped(
                $"Covers the last {estate.HistoryDays} days rather than the reporting period: an agent nobody "
                + "has touched for months is exactly what an inventory review is looking for, and would be "
                + "invisible in a short window. That window is deliberately shorter than the analysis history - "
                + $"it only has to reach past the {summary.Options.AgentRetireInactiveDays}-day retirement line. "
                + "An agent only appears once it has been invoked: the audit log records agents that were used, "
                + "not agents that exist."));
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Measure", "Value");
            sheet.AddRow("Active agents (this period)", estate.ActiveAgents);
            sheet.AddRow("Known agents (history)", estate.KnownAgents);
            sheet.AddRow("Custom-built agents", estate.CustomAgents);
            sheet.AddRow("Agent users", estate.AgentUsers);
            sheet.AddRow("Of which licensed", estate.LicensedAgentUsers);
            sheet.AddRow("Agent interactions", estate.AgentInteractions);
            sheet.AddRow("Interactions per agent user", estate.InteractionsPerAgentUser);
            sheet.AddRow("Most used agent", estate.MostPopularAgent ?? "-");
            sheet.AddRow("Most versatile agent", estate.MostVersatileAgent ?? "-");

            if (estate.HealthBreakdown.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Verdict", "Agents", "What it means");

                var first = sheet.CurrentRow + 1;
                foreach (var health in estate.HealthBreakdown)
                {
                    sheet.AddRow(health.Label, health.Value, XlsxCell.Wrapped(HealthMeaning(health.Label, summary.Options)));
                }
                var last = sheet.CurrentRow;

                var chart = new XlsxChart
                {
                    Type = XlsxChartType.Doughnut,
                    Title = "Agent inventory health",
                    // Anchored clear of column K: the inventory table below is 11 columns wide, so a
                    // chart at K would sit on top of the "Why" column.
                    AnchorCell = "M3",
                    CategoryRange = sheet.RangeReference(first, 1, last, 1),
                    ShowLegend = true,
                };
                chart.AddSeries("Agents", sheet.RangeReference(first, 2, last, 2));
                sheet.AddChart(chart);
            }

            if (estate.Agents.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Agent", "Type", "Users", "Licensed users", "Interactions",
                    "Per user", "Surfaces", "Last used", "Days since", "Verdict", "Why");

                var headerRow = sheet.CurrentRow;
                foreach (var agent in estate.Agents)
                {
                    sheet.AddRow(
                        agent.Name,
                        agent.IsCustomAgent ? "Custom" : "Microsoft",
                        agent.Users,
                        agent.LicensedUsers,
                        agent.Interactions,
                        agent.InteractionsPerUser,
                        agent.AppsUsed,
                        agent.LastUsedUtc.HasValue ? XlsxCell.Date(agent.LastUsedUtc.Value) : (object)"-",
                        agent.DaysSinceLastUse.HasValue ? (object)agent.DaysSinceLastUse.Value : "-",
                        agent.HealthName,
                        XlsxCell.Wrapped(agent.HealthReason));
                }

                sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 11);
            }
        }

        private static string HealthMeaning(string health, CopilotAdoptionOptions o)
        {
            switch (health)
            {
                case "Keep":
                    return $"Used within {o.AgentReviewInactiveDays} days by at least {o.AgentMinUsers} people. Genuinely adopted.";
                case "New":
                    return $"First seen within the last {o.AgentNewDays} days. Too new to judge, and deliberately exempt from review.";
                case "Review":
                    return $"Going quiet ({o.AgentReviewInactiveDays}-{o.AgentRetireInactiveDays} days), or current but used by fewer than {o.AgentMinUsers} people.";
                case "Retire":
                    return $"Unused for {o.AgentRetireInactiveDays} days or more. Confirm with its owner, then remove it.";
                default:
                    return string.Empty;
            }
        }

        private static void WriteUnlicensedSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var unlicensed = summary.Unlicensed;
            if (unlicensed == null || unlicensed.ActiveUsers == 0) return;

            var sheet = workbook.AddSheet("Unlicensed usage");
            sheet.SetColumnWidths(34, 16, 16, 30);

            sheet.AddTitle("Unlicensed Copilot Chat");
            sheet.AddRow(XlsxCell.Wrapped(
                "People with no Copilot licence who use Copilot anyway. This is the one Copilot population "
                + "Microsoft's own reporting cannot see at all, and the strongest evidence of unmet demand "
                + "available - these people chose to use Copilot with no licence, no training and no prompting."));
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Measure", "Value");
            sheet.AddRow("Unlicensed Copilot users", unlicensed.ActiveUsers);
            sheet.AddRow("Interactions", unlicensed.Interactions);
            sheet.AddRow("Interactions per user per month", unlicensed.InteractionsPerUserPerMonth);
            sheet.AddRow("Using agents", unlicensed.AgentUsers);
            if (unlicensed.Truncated)
            {
                sheet.AddRow("Note", XlsxCell.Wrapped(
                    "The unlicensed population hit its row cap, so these figures are a floor rather than a total."));
            }

            if (unlicensed.HabitBuckets.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Usage frequency", "Users", "% of active", "Range");

                var first = sheet.CurrentRow + 1;
                foreach (var bucket in unlicensed.HabitBuckets)
                {
                    sheet.AddRow(bucket.Label, bucket.Users, bucket.SharePct, bucket.RangeLabel);
                }
                var last = sheet.CurrentRow;

                var chart = new XlsxChart
                {
                    Type = XlsxChartType.Column,
                    Title = "How often unlicensed users open Copilot",
                    CategoryRange = sheet.RangeReference(first, 1, last, 1),
                    AnchorCell = "G3",
                    ShowDataLabels = true,
                    ShowLegend = false,
                };
                chart.AddSeries("Users", sheet.RangeReference(first, 2, last, 2));
                sheet.AddChart(chart);
            }

            if (unlicensed.UsageByApp.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Copilot surface", "Interactions");
                foreach (var app in unlicensed.UsageByApp)
                {
                    sheet.AddRow(app.Label, app.Value);
                }
            }

            if (unlicensed.UsageByDepartment.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Department", "Interactions");
                foreach (var dept in unlicensed.UsageByDepartment)
                {
                    sheet.AddRow(dept.Label, dept.Value);
                }
            }
        }

        #endregion

        #region Action plan and user lists

        private static void WriteActionPlanSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            if (summary.ActionPlan.Count == 0) return;

            var sheet = workbook.AddSheet("Enablement plan");
            sheet.SetColumnWidths(26, 12, 14, 78, 42, 70);

            sheet.AddTitle("Enablement plan");
            sheet.AddRow(XlsxCell.Wrapped(
                "Every licensed user needs exactly one of these next steps, so the counts add up to the whole "
                + "scored population. This is the size of each job."));
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Action", "Users", "% of licensed", "What it means and why these users qualify",
                "Microsoft guidance resources", "Microsoft guidance URLs");

            var first = sheet.CurrentRow + 1;
            foreach (var action in summary.ActionPlan)
            {
                sheet.AddRow(
                    action.Label,
                    action.Users,
                    action.SharePct,
                    XlsxCell.Wrapped(action.Description),
                    XlsxCell.Wrapped(GuidanceTitles(action.GuidanceLinks)),
                    XlsxCell.Wrapped(GuidanceUrls(action.GuidanceLinks)));
            }
            var last = sheet.CurrentRow;

            var chart = new XlsxChart
            {
                Type = XlsxChartType.Bar,
                Title = "How many people need each action",
                CategoryRange = sheet.RangeReference(first, 1, last, 1),
                AnchorCell = "F3",
                ShowDataLabels = true,
                ShowLegend = false,
            };
            chart.AddSeries("Users", sheet.RangeReference(first, 2, last, 2));
            sheet.AddChart(chart);
        }

        private static void WriteLicensedUsersSheet(XlsxWriter workbook, CopilotAdoptionAnalysis analysis)
        {
            var users = analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>();
            if (users.Count == 0) return;

            var sheet = workbook.AddSheet("Licensed users");
            sheet.SetColumnWidths(34, 26, 22, 22, 12, 10, 16, 34, 18, 42, 14, 12, 10, 10, 14, 14, 22, 60, 42, 70);

            // A workbook that quietly stops at a row limit is worse than one that refuses to export:
            // the reader has no way of knowing the list is short. Say so on the sheet itself, where it
            // cannot be missed, rather than only in a warnings collection.
            var truncated = users.Count > MaxUserRows;
            if (truncated)
            {
                sheet.AddTitle("Licensed users - TRUNCATED");
                sheet.AddRow(XlsxCell.Wrapped(
                    $"This sheet lists the {MaxUserRows:N0} least-engaged of {users.Count:N0} licensed users. "
                    + "The rest are omitted to keep the workbook openable. Every summary figure elsewhere in "
                    + "this file covers the whole population - only this list is shortened. Use the CSV export "
                    + "on the Licensed users tab if you need all of them."));
                sheet.AddBlankRow();
            }
            else if (analysis.Summary.FiguresIncomplete)
            {
                sheet.AddTitle("Licensed users - FIGURES INCOMPLETE");
                sheet.AddRow(XlsxCell.Wrapped(
                    "A source query failed or timed out. This sheet is incomplete and must not be used as a complete employee list."));
                sheet.AddBlankRow();
            }

            sheet.AddHeaderRow(
                "User", "Department", "Job title", "Manager", "Engagement", "Band", "Signal source",
                "Source comparison", "Figures incomplete", "Figure warnings", "Interactions",
                "Active days", "Expected", "Apps", "Used Cowork", "Days since last use", "Recommended action",
                "Action detail", "Microsoft guidance resources", "Microsoft guidance URLs");

            var headerRow = sheet.CurrentRow;

            foreach (var user in users.OrderBy(u => u.AdoptionScore).Take(MaxUserRows))
            {
                sheet.AddRow(
                    user.UserPrincipalName,
                    user.Department ?? string.Empty,
                    user.JobTitle ?? string.Empty,
                    user.ManagerUserPrincipalName ?? string.Empty,
                    user.AdoptionScore,
                    user.BandName,
                    user.SignalSource,
                    XlsxCell.Wrapped(SourceComparisonSummary(user, analysis.Summary)),
                    analysis.Summary.FiguresIncomplete ? "Yes" : "No",
                    XlsxCell.Wrapped(WarningSummary(analysis.Summary)),
                    user.Interactions,
                    user.ActiveDays,
                    user.ExpectedActiveDays,
                    user.AppsUsed,
                    user.UsedCowork ? "Yes" : "No",
                    user.DaysSinceLastUse.HasValue ? (object)user.DaysSinceLastUse.Value : "-",
                    user.RecommendedActionLabel,
                    XlsxCell.Wrapped(user.RecommendedAction),
                    XlsxCell.Wrapped(CopilotAdoptionGuidanceCatalogue.TitlesForAction(user.RecommendedActionCode)),
                    XlsxCell.Wrapped(CopilotAdoptionGuidanceCatalogue.UrlsForAction(user.RecommendedActionCode)));
            }

            sheet.FreezeTopRows(headerRow);
            sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 20);
        }

        /// <summary>
        /// The Cowork readiness view: who already uses Cowork, who should be enabled next, and the
        /// department order to roll it out in.
        /// </summary>
        private static void WriteCoworkSheet(XlsxWriter workbook, CopilotAdoptionAnalysis analysis)
        {
            var summary = analysis.Summary;
            if (!summary.CoworkReadinessAvailable) return;

            var rows = analysis.CoworkReadiness ?? new List<CoworkReadinessRow>();
            if (rows.Count == 0) return;

            var sheet = workbook.AddSheet("Cowork readiness");
            sheet.SetColumnWidths(34, 26, 22, 22, 16, 14, 14, 12, 12, 12, 60);

            sheet.AddTitle("Microsoft 365 Copilot Cowork - readiness");
            sheet.AddRow(XlsxCell.Wrapped(
                "Cowork has no licence of its own. It requires a Microsoft 365 Copilot licence as a "
                + "prerequisite and is then billed by usage against Copilot Credits, with access granted by a "
                + "spending policy scoped to users or groups. So this is not a list of licences to buy - it is "
                + "a list of people to put in that policy."));
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Measure", "Value", "What it means");
            var tierFirst = sheet.CurrentRow + 1;

            foreach (var tier in summary.CoworkTiers)
            {
                AddMeta(sheet, tier.Label, tier.Users,
                    (tier.Basis == CopilotAdoptionScoring.CoworkBasis.Evidence
                        ? "OBSERVED. "
                        : "PREDICTED, not observed. ")
                    + tier.Description);
            }

            var tierLast = sheet.CurrentRow;

            sheet.AddBlankRow();
            AddMeta(sheet, "Average coordination load", summary.CoworkAverageCoordinationLoad,
                "Mean of the 0-100 coordination-load score across scored seat holders - how much delegable, "
                + "multi-step work the population carries.");
            AddMeta(sheet, "Average Copilot fluency", summary.CoworkAverageFluency,
                "Mean of the 0-100 fluency score - whether people are practised enough with Copilot to "
                + "delegate a multi-step task to it.");

            if (summary.CoworkCreditPosition != null && summary.CoworkCreditPosition.Available)
            {
                var credits = summary.CoworkCreditPosition;
                sheet.AddBlankRow();
                sheet.AddRow(XlsxCell.Wrapped(
                    "Copilot Credit position - the SHARED pool, not Cowork-only spend. Cowork draws on it, "
                    + "which is what makes it valid rollout headroom, but Copilot Studio and other "
                    + "credit-billed workloads draw on the same pool and Microsoft publishes no way to "
                    + "separate them."));

                if (credits.Entitled.HasValue)
                {
                    AddMeta(sheet, "Credits entitled", credits.Entitled.Value, "Pre-purchased capacity.");
                }
                if (credits.Consumed.HasValue)
                {
                    AddMeta(sheet, "Credits consumed", credits.Consumed.Value, "Consumed so far, across all credit-billed workloads.");
                }
                if (credits.AvailableCredits.HasValue)
                {
                    AddMeta(sheet, "Credits available", credits.AvailableCredits.Value, "Remaining headroom for a Cowork rollout.");
                }
                if (credits.PayAsYouGoConsumed.HasValue)
                {
                    AddMeta(sheet, "Pay-as-you-go consumed", credits.PayAsYouGoConsumed.Value, "Billed beyond pre-purchased capacity.");
                }
                if (!string.IsNullOrWhiteSpace(credits.Status))
                {
                    AddMeta(sheet, "Capacity status", credits.Status, "As reported by Microsoft, e.g. WithinCapacity or Overage.");
                }
            }

            var chart = new XlsxChart
            {
                Type = XlsxChartType.Bar,
                Title = "Cowork readiness tiers",
                CategoryRange = sheet.RangeReference(tierFirst, 1, tierLast, 1),
                AnchorCell = "E5",
                ShowDataLabels = true,
                ShowLegend = false,
            };
            chart.AddSeries("Users", sheet.RangeReference(tierFirst, 2, tierLast, 2));
            sheet.AddChart(chart);

            // Rollout sequencing.
            if (summary.CoworkByDepartment.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddBlankRow();
                sheet.AddTitle("Rollout order by department");
                sheet.AddRow(XlsxCell.Wrapped(
                    "Ordered by the NUMBER of prime candidates, not the rate. A three-person team where "
                    + "everyone qualifies is a 100% rate and not where a rollout should start."));
                sheet.AddHeaderRow(
                    "Department", "Copilot seats", "Prime candidates", "Prime candidate %",
                    "Regular Cowork users", "Avg coordination load", "Avg fluency");

                foreach (var segment in summary.CoworkByDepartment)
                {
                    sheet.AddRow(
                        segment.Segment,
                        segment.LicensedUsers,
                        segment.PrimeCandidates,
                        segment.PrimeCandidateRatePct,
                        segment.RegularCoworkUsers,
                        segment.AverageCoordinationLoad,
                        segment.AverageFluency);
                }
            }

            // The people.
            sheet.AddBlankRow();
            sheet.AddBlankRow();

            var truncated = rows.Count > MaxUserRows;
            sheet.AddTitle(truncated ? "Cowork candidates - TRUNCATED" : "Cowork candidates");
            if (truncated)
            {
                sheet.AddRow(XlsxCell.Wrapped(
                    $"This list shows {MaxUserRows:N0} of {rows.Count:N0} scored seat holders, everyone "
                    + "with OBSERVED Cowork use first so the people already proving the capability works "
                    + "cannot be truncated away by inferred candidates. "
                    + "The summary figures above cover the whole population - only this list is shortened. "
                    + "Use the CSV export on the Cowork tab if you need all of them."));
            }

            sheet.AddHeaderRow(
                "User", "Department", "Job title", "Manager", "Cowork tier", "Verdict based on",
                "Coordination load", "Copilot fluency", "Cowork report tasks", "Cowork automation %",
                "Cowork audit interactions", "Justification");

            var headerRow = sheet.CurrentRow;

            // Evidence before inference, but ONLY when the cap actually bites - OrderBy is stable, so an
            // untruncated sheet keeps FinaliseCowork's order untouched. This is the same protection
            // CoworkReadinessSql's coworkFirstOrder applies to its own TOP (@maxRows) and that
            // WriteOpportunitiesSheet applies to proven demand; without it this cap re-opens the hole
            // one layer up, because FinaliseCowork ranks the recommended block by coordination load and
            // an established Cowork user with a quiet calendar sorts below thousands of busier
            // non-users. Dropping them here would scope them out of the spending policy an admin builds
            // from this sheet - i.e. revoke access from existing users.
            var listed = truncated
                ? rows.OrderBy(r => r.Basis == CopilotAdoptionScoring.CoworkBasis.Evidence ? 0 : 1)
                    .Take(MaxUserRows)
                : rows.Take(MaxUserRows);

            foreach (var row in listed)
            {
                sheet.AddRow(
                    row.UserPrincipalName,
                    row.Department ?? string.Empty,
                    row.JobTitle ?? string.Empty,
                    row.ManagerUserPrincipalName ?? string.Empty,
                    row.TierLabel,
                    row.Basis,
                    row.CoordinationLoadScore,
                    row.FluencyScore,
                    row.CoworkReportTotalTasks,
                    row.CoworkAutomationRatioPct,
                    row.CoworkInteractions,
                    XlsxCell.Wrapped(row.Rationale));
            }

            sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 11);
        }

        /// <summary>
        /// The modelled value estimate, on its own sheet and nowhere else.
        ///
        /// <para>Deliberately separated from every measured figure in this workbook. The observed volumes
        /// are real; the conversion to hours is an assumption, and a reader who finds an "hours saved"
        /// column sitting beside interaction counts will reasonably assume both were counted. Keeping it
        /// on a sheet that states its assumptions at the top is what makes the number usable without
        /// making it misleading.</para>
        /// </summary>
        private static void WriteCoworkEstimateSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var estimate = summary.CoworkValueEstimate;
            if (!summary.CoworkReadinessAvailable || estimate == null || estimate.CohortUsers == 0) return;

            var sheet = workbook.AddSheet("Cowork estimate (modelled)");
            sheet.SetColumnWidths(38, 18, 62);

            sheet.AddTitle("Cowork value estimate - MODELLED, NOT MEASURED");
            sheet.AddRow(XlsxCell.Wrapped(
                "This product does not and cannot measure time saved. The volumes below are observed from "
                + "Microsoft's usage reports; the hours are those volumes multiplied by an editable "
                + "assumption. Treat this as a way to size a rollout, not as a result. Do not quote the "
                + "hours or the money without the assumptions listed underneath them."));
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Measure", "Value", "What it means");

            AddMeta(sheet, "Cohort size", estimate.CohortUsers,
                "People recommended for the Cowork spending policy - prime candidates plus existing users.");

            sheet.AddBlankRow();
            AddMeta(sheet, "Meetings a month (observed)", estimate.AddressableMeetings,
                "OBSERVED. Total meetings across the cohort, from Microsoft's Teams usage report.");
            AddMeta(sheet, "Emails a month (observed)", estimate.AddressableMailThreads,
                "OBSERVED. Total emails sent and read across the cohort.");
            AddMeta(sheet, "Document touches a month (observed)", estimate.AddressableDocuments,
                "OBSERVED. SharePoint and OneDrive files viewed or edited across the cohort.");

            sheet.AddBlankRow();
            AddMeta(sheet, "Modelled hours a month (low)", estimate.HoursPerMonthLow,
                "MODELLED. The lower bound of the assumption range.");
            AddMeta(sheet, "Modelled hours a month (high)", estimate.HoursPerMonthHigh,
                "MODELLED. The upper bound. Quote the range, never a single figure.");

            sheet.AddBlankRow();
            sheet.AddTitle("Assumptions");
            foreach (var assumption in estimate.Assumptions)
            {
                sheet.AddRow(XlsxCell.Wrapped(assumption));
            }
        }

        private static void WriteOpportunitiesSheet(XlsxWriter workbook, CopilotAdoptionAnalysis analysis)
        {
            var candidates = analysis.Opportunities ?? new List<LicenceOpportunityRow>();
            if (candidates.Count == 0) return;

            var sheet = workbook.AddSheet("Licence opportunities");
            sheet.SetColumnWidths(34, 26, 22, 14, 14, 18, 42, 22, 14, 14, 14, 60, 42, 70);

            if (candidates.Count > MaxUserRows)
            {
                sheet.AddTitle("Licence opportunities - TRUNCATED");
                sheet.AddRow(XlsxCell.Wrapped(
                    $"This sheet lists the {MaxUserRows:N0} strongest of {candidates.Count:N0} candidates, "
                    + "proven-demand candidates first so recurrent unlicensed Copilot users cannot be truncated "
                    + "away by people who have never used it. The headline 'recommended for a licence' figure "
                    + "covers all of them."));
                sheet.AddBlankRow();
            }
            else if (analysis.Summary.FiguresIncomplete)
            {
                sheet.AddTitle("Licence opportunities - FIGURES INCOMPLETE");
                sheet.AddRow(XlsxCell.Wrapped(
                    "A source query failed or timed out. This sheet is incomplete and must not be used as a complete employee list."));
                sheet.AddBlankRow();
            }

            sheet.AddHeaderRow(
                "User", "Department", "Job title", "Business case", "Recommended",
                "Figures incomplete", "Figure warnings", "Unlicensed Copilot interactions", "Teams", "Email", "Files", "Justification",
                "Microsoft guidance resources", "Microsoft guidance URLs");

            var headerRow = sheet.CurrentRow;

            // Proven demand first, then score - the same order the service and the CSV use. Re-sorting on
            // score alone here would undo it precisely where it matters most: this sheet TRUNCATES, and a
            // proven-demand candidate can score below the recommendation bar by construction (the Copilot
            // weight sits under it), so a large tenant's workbook would drop people who already use
            // Copilot in favour of people who never have.
            foreach (var candidate in candidates
                .OrderBy(c => c.QualificationTier == CopilotAdoptionScoring.OpportunityTiers.ProvenDemand ? 0 : 1)
                .ThenByDescending(c => c.OpportunityScore)
                .ThenBy(c => c.UserPrincipalName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxUserRows))
            {
                sheet.AddRow(
                    candidate.UserPrincipalName,
                    candidate.Department ?? string.Empty,
                    candidate.JobTitle ?? string.Empty,
                    candidate.OpportunityScore,
                    candidate.Recommended ? "Yes" : "No",
                    analysis.Summary.FiguresIncomplete ? "Yes" : "No",
                    XlsxCell.Wrapped(WarningSummary(analysis.Summary)),
                    candidate.UnlicensedCopilotInteractions,
                    candidate.TeamsMessages + candidate.TeamsMeetings,
                    candidate.EmailsSent + candidate.EmailsRead,
                    candidate.FilesViewedOrEdited,
                    XlsxCell.Wrapped(candidate.Rationale),
                    XlsxCell.Wrapped(CopilotAdoptionGuidanceCatalogue.TitlesForAction(CopilotAdoptionGuidanceCatalogue.UnlicensedActionCode)),
                    XlsxCell.Wrapped(CopilotAdoptionGuidanceCatalogue.UrlsForAction(CopilotAdoptionGuidanceCatalogue.UnlicensedActionCode)));
            }

            sheet.FreezeTopRows(headerRow);
            sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 14);
        }

        private static string GuidanceTitles(IEnumerable<AdoptionGuidanceLink> links)
        {
            return string.Join(" | ", (links ?? Enumerable.Empty<AdoptionGuidanceLink>()).Select(l => l.Title));
        }

        private static string GuidanceUrls(IEnumerable<AdoptionGuidanceLink> links)
        {
            return string.Join(" | ", (links ?? Enumerable.Empty<AdoptionGuidanceLink>()).Select(l => l.Url));
        }

        #endregion

        #region Methodology

        /// <summary>
        /// The formulas, written out. Without these the workbook is a set of numbers whose provenance
        /// dies the moment it leaves the browser - and this file is explicitly meant to be circulated
        /// and compared months later, by which point nobody remembers what "habitual" meant.
        /// </summary>
        private static void WriteMethodSheet(XlsxWriter workbook, CopilotAdoptionSummary summary)
        {
            var sheet = workbook.AddSheet("How this is calculated");
            sheet.SetColumnWidths(30, 96);

            var o = summary.Options;
            // The EXACT value the scorer divides by, not a rounded display figure: a rounded denominator
            // in a published formula puts the arithmetic on the wrong side of a band boundary for a user
            // sitting exactly on it, in the one sheet whose purpose is to reproduce the score.
            var targetDays = CopilotAdoptionScoring.TargetActiveDays(o);
            var targetDaysLabel = Math.Round(targetDays, 1);
            var weightSum = o.FrequencyWeight + o.DepthWeight + o.BreadthWeight;

            sheet.AddTitle("How this is calculated");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Measure", "Definition");

            AddMethod(sheet, "Engagement score",
                "Each licensed user scores 0-100 from three capped components, because 'did they use Copilot?' is "
                + "almost never a yes/no question - someone who opened it twice and someone who lives in it produce "
                + "the same 'active user' count and need opposite responses.\n"
                + $"frequency  = min(1, activeDays / {targetDays})\n"
                + $"confidence = min(1, activeDays / {o.DepthMinActiveDays})\n"
                + $"depth      = min(1, (interactions / activeDays) / {o.DepthTargetInteractionsPerActiveDay}) x confidence\n"
                + $"breadth    = min(1, appsUsed / {o.BreadthTargetApps})\n"
                + $"score = (frequency x {o.FrequencyWeight} + depth x {o.DepthWeight} + breadth x {o.BreadthWeight}) / {weightSum} x 100\n"
                + $"Depth is scaled down below {o.DepthMinActiveDays} active days, because it divides by a number "
                + "the user controls: a handful of prompts in one afternoon would otherwise score full marks for "
                + "depth and read as a habit forming. At or above that many active days nothing changes.\n"
                + $"The {targetDaysLabel}-day frequency target above is the full-window one. An account younger than the "
                + "reporting period has its target prorated to the days it has actually existed, so each row's own "
                + "'Expected active days' column is the number that row was scored against.");

            AddMethod(sheet, "Why working days",
                $"The frequency target is {o.FrequencyTargetRatio:P0} of the working days in the period, assuming "
                + $"{o.WorkingDaysPerWeek} working days a week - {targetDaysLabel} days over {o.WindowDays}. Measured "
                + "against calendar days, someone who used Copilot every single working day would cap out at about "
                + "71% and look like a partial adopter.");

            AddMethod(sheet, "Bands",
                $"Champion at {o.ChampionScore}+, Established at {o.EstablishedScore}+, Developing at "
                + $"{o.DevelopingScore}+, Trialling below that. Users with no activity in the period are not scored "
                + $"at all: Dormant means they used Copilot within the last {o.HistoryDays} days but not in this "
                + "period; Never used means no activity anywhere in that history. The distinction decides the "
                + "action - one needs a conversation, the other needs onboarding or the licence back.");

            AddMethod(sheet, "Habitual users",
                $"Engagement of {o.EstablishedScore} or more. This is the figure that tracks realised value - the "
                + "adoption rate can sit at 100% while this sits near zero, which is exactly what a renewal "
                + "conversation needs to surface.");

            AddMethod(sheet, "How often people open Copilot",
                $"Active days restated per {o.HabitBucketNormalisationDays}-day month and rounded to whole days, so "
                + "the buckets mean the same thing whichever period was selected:\n"
                + $"daysPerMonth = round(activeDays x {o.HabitBucketNormalisationDays} / {o.WindowDays})\n"
                + "This is UNWEIGHTED frequency and is deliberately NOT the same measure as 'habitual users' above, "
                + "which is the weighted engagement score. The two are meant to be compared: a large Daily figure "
                + "with a low habit rate means people open Copilot constantly and do very little with it.\n"
                + "Percentages are of ACTIVE users. A licence that was never used is not 'infrequent' - it is "
                + "an idle seat, assessed by the reclaim confidence tiers, and merging the two hides the more expensive problem.");

            AddMethod(sheet, "Usage concentration",
                "Active licensed users ranked by interaction count and cut into percentile cohorts. Only active "
                + "users are ranked - including idle licences would put every one of them in the bottom cohort at zero "
                + "and give every tenant an identical chart.");

            AddMethod(sheet, "Business case score",
                "Unlicensed users score 0-100 on four weighted signals, weighted so evidence beats inference:\n"
                // Written as the computation, not as a rounded product. Printing "64.3" at D90 would make
                // the published formula disagree with the scorer at the recommendation boundary - a
                // candidate the scorer puts at exactly 50.0 reads as 49.9 here. This form is exactly
                // reproducible at every window and says where the number comes from.
                + $"copilot = min(1, unlicensedCopilotInteractions / ({o.OpportunityCopilotTarget} x {o.WindowDays} / {o.OpportunityCopilotTargetBasisDays})) x {o.OpportunityUnlicensedCopilotWeight}\n"
                + $"collaboration = min(1, (teamsMessages + teamsMeetings) / {o.OpportunityCollaborationTarget}) x {o.OpportunityCollaborationWeight}\n"
                + $"email = min(1, (emailsSent + emailsRead) / {o.OpportunityEmailTarget}) x {o.OpportunityEmailWeight}\n"
                + $"documents = min(1, filesViewedOrEdited / {o.OpportunityDocumentTarget}) x {o.OpportunityDocumentWeight}\n"
                + $"The Copilot target is {o.OpportunityCopilotTarget} interactions per {o.OpportunityCopilotTargetBasisDays} days, "
                + $"scaled to the {o.WindowDays}-day window shown above - about {Math.Round(CopilotAdoptionScoring.OpportunityCopilotTargetForWindow(o), 1)}. "
                + "The formula keeps the exact division rather than that rounded figure, because rounding it "
                + "would put the published arithmetic on the wrong side of the recommendation bar for a "
                + "candidate sitting exactly on it. Without the scaling the same person "
                + "would be recommended over a long window and not over a short one.\n"
                + $"Recommended when unlicensedCopilotActiveDays is at least {o.OpportunityProvenDemandMinActiveDays} "
                + $"(proven demand), or when the score reaches {o.OpportunityRecommendScore} (workload inferred). "
                + "Proven demand qualifies on its own because the Copilot weight "
                + $"({o.OpportunityUnlicensedCopilotWeight}) sits below the score bar, so the one signal that "
                + "actually proves demand for Copilot could otherwise never clear it while general Microsoft 365 "
                + "busyness could. Each row states which route qualified it.");

            AddMethod(sheet, "Agent verdicts",
                $"Retire after {o.AgentRetireInactiveDays} days without use; Review between {o.AgentReviewInactiveDays} "
                + $"and {o.AgentRetireInactiveDays} days, or while current but used by fewer than {o.AgentMinUsers} "
                + $"people; Keep when used within {o.AgentReviewInactiveDays} days by at least {o.AgentMinUsers} "
                + $"people. Any agent first seen within {o.AgentNewDays} days is New and exempt from review - a "
                + "brand-new agent with two users has not failed, it has not started.");

            AddMethod(sheet, "Why our figures differ from Microsoft's",
                "The Copilot audit log and Microsoft's Copilot usage report answer different questions. "
                + "Audit-log figures in this workbook cover every user, including unlicensed Copilot Chat users, "
                + $"and are counted over the selected D{o.WindowDays} window. Microsoft's report covers licensed "
                + "users only and uses Microsoft's own settled report window, so the two will legitimately differ. "
                + "Microsoft states that audit-log aggregates are not intended to match the official usage report, "
                + "(https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft-365-copilot-usage?view=o365-worldwide#whats-the-difference-between-the-user-activity-table-and-audit-log), "
                + "but also states that unlicensed Copilot Chat usage is not available through Microsoft Graph reports APIs; "
                + "(https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/api/admin-settings/reports/copilotreportroot-getmicrosoft365copilotusageuserdetail). "
                + "Audit data via Purview or the Office 365 Management Activity API is the programmatic route for that signal. "
                + "Where both sources cover the same licensed user, the Licensed users sheet shows both figures side by side "
                + "with their source and window. Do not average or silently reconcile them into one number.");

            AddMethod(sheet, "Comparing two snapshots",
                "These figures are only comparable between two runs if both used the same thresholds and the same "
                + "period length. Both are recorded on the Report sheet - check them before subtracting one file "
                + "from another, because the tuning is adjustable and 'adoption went up' must not turn out to mean "
                + "'the bar moved'.");

            AddMethod(sheet, "Licence classification",
                "Microsoft ships Copilot-branded SKUs that are not a Microsoft 365 Copilot licence (Copilot Studio, "
                + "Copilot for Sales), and ships new licence SKUs regularly. Everything found is listed below, "
                + "including what was excluded, so the licensed population can be checked rather than trusted.");

            if (summary.SeatLicenceTypes.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Product", "SKU", "Assigned users", "Purchased", "Unassigned", "Assigned idle", "Purchased refreshed UTC", "Counted as a Copilot licence");
                foreach (var licence in summary.SeatLicenceTypes)
                {
                    sheet.AddRow(licence.Name, licence.SkuPartNumber, licence.AssignedUsers,
                        licence.PurchasedUnits.HasValue ? (object)licence.PurchasedUnits.Value : "Unknown",
                        licence.UnassignedUnits.HasValue ? (object)licence.UnassignedUnits.Value : "Unknown",
                        licence.AssignedIdleUsers,
                        licence.PurchasedUnitsRefreshedUtc.HasValue ? licence.PurchasedUnitsRefreshedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                        licence.IsCopilotSeat ? "Yes" : "No");
                }
            }

            sheet.FreezeTopRows(3);
        }

        private static string SourceComparisonSummary(LicensedUserAdoptionRow user, CopilotAdoptionSummary summary)
        {
            if (user == null || summary == null || !user.SourceComparisonAvailable) return string.Empty;

            var reportPeriod = summary.DataSources.CopilotUsageReportPeriodDays > 0
                ? $"D{summary.DataSources.CopilotUsageReportPeriodDays}"
                : "Microsoft report window";
            var reportDate = summary.DataSources.CopilotUsageReportDate.HasValue
                ? $", snapshot {summary.DataSources.CopilotUsageReportDate.Value:yyyy-MM-dd}"
                : string.Empty;

            return $"Audit log (selected D{summary.WindowDays}): {user.AuditInteractions:N0} interactions, "
                + $"{user.AuditActiveDays:N0} active days, {user.AuditAppsUsed:N0} apps. "
                + $"Microsoft Copilot usage report ({reportPeriod}{reportDate}): "
                + $"{(user.ReportPrompts.HasValue ? user.ReportPrompts.Value.ToString("N0", CultureInfo.InvariantCulture) : "-")} prompts, "
                + $"{(user.ReportActiveDays.HasValue ? user.ReportActiveDays.Value.ToString("N0", CultureInfo.InvariantCulture) : "-")} active days.";
        }

        private static void AddMethod(XlsxSheet sheet, string name, string definition)
        {
            sheet.AddRow(name, XlsxCell.Wrapped(definition));
        }

        #endregion

        private static double Percentage(double part, double total)
        {
            return total <= 0 ? 0 : Math.Round(part / total * 100d, 1, MidpointRounding.AwayFromZero);
        }
    }
}

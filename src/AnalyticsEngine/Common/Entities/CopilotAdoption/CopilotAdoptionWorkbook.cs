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
                WriteFunnelSheet(workbook, summary);
                WriteEngagementSheet(workbook, summary);
                WriteTrendSheet(workbook, summary);
                WriteDepartmentSheet(workbook, summary);
                WriteAgentSheet(workbook, summary);
                WriteUnlicensedSheet(workbook, summary);
                WriteActionPlanSheet(workbook, summary);
                WriteLicensedUsersSheet(workbook, analysis);
                WriteOpportunitiesSheet(workbook, analysis);
                WriteMethodSheet(workbook, summary);

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
                    ? $"Of {summary.LicensedUsers:N0} seats. Rates in this workbook are of the analysed users only."
                    : "Every licensed user was analysed.");
            AddMeta(sheet, "From (UTC)", summary.FromUtc, string.Empty);
            AddMeta(sheet, "To (UTC)", summary.ToUtc, string.Empty);
            AddMeta(sheet, "History window", $"{summary.Options.HistoryDays} days",
                "How far back 'ever used Copilot' looks, which is what separates Dormant from Never used.");

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
                    ? $"Snapshot of {summary.DataSources.M365UsageReportDate.Value:yyyy-MM-dd}."
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
            AddMeta(sheet, "Breadth target", o.BreadthTargetApps, "Copilot surfaces for full marks.");
            AddMeta(sheet, "Champion at", o.ChampionScore, "Engagement score for the Champion band.");
            AddMeta(sheet, "Established at", o.EstablishedScore, "The 'habit formed' line - what 'habitual users' counts.");
            AddMeta(sheet, "Developing at", o.DevelopingScore, "Engagement score for the Developing band.");
            AddMeta(sheet, "Habit month length", o.HabitBucketNormalisationDays, "Active days are restated per this many days.");
            AddMeta(sheet, "Licence recommendation at", o.OpportunityRecommendScore, "Business-case score for a recommended candidate.");
            AddMeta(sheet, "Agent review after", $"{o.AgentReviewInactiveDays} days", "Inactivity before an agent is reviewed.");
            AddMeta(sheet, "Agent retire after", $"{o.AgentRetireInactiveDays} days", "Inactivity before an agent is proposed for retirement.");
            AddMeta(sheet, "Agent minimum users", o.AgentMinUsers, "Users an agent needs before its use counts as adoption.");

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

        private static void AddMeta(XlsxSheet sheet, string name, object value, string notes)
        {
            sheet.AddRow(name, value, XlsxCell.Wrapped(notes));
        }

        private static string YesNo(bool value)
        {
            return value ? "Yes" : "No";
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
                "Users holding at least one licence classified as a Microsoft 365 Copilot seat. The true seat count.");
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
                $"No Copilot activity anywhere in the last {summary.Options.HistoryDays} days. Needs onboarding, or the seat back.");
            AddMeta(sheet, "Reclaimable seats", summary.ReclaimableSeats,
                "Dormant plus never used - seats that produced nothing this period.");

            var last = sheet.CurrentRow;

            sheet.AddBlankRow();
            AddMeta(sheet, "Adoption rate %", summary.AdoptionRatePct, "Active users as a share of the users analysed.");
            AddMeta(sheet, "Habit rate %", summary.HabitRatePct, "Habitual users as a share of the users analysed.");
            AddMeta(sheet, "Average engagement", summary.AverageAdoptionScore, "Mean score out of 100, including seats scoring zero.");
            AddMeta(sheet, "Median engagement", summary.MedianAdoptionScore,
                "Reported next to the mean because a few Champions pull the mean up; a large gap means a long tail of light users.");
            AddMeta(sheet, "Total interactions", summary.TotalInteractions, "All Copilot interactions by licensed users in the period.");

            sheet.AddBlankRow();
            AddMeta(sheet, "Using Copilot unlicensed", summary.UnlicensedActiveUsers,
                "People with no seat who used Copilot anyway - proven, unmet demand, and invisible in Microsoft's own reports.");
            AddMeta(sheet, "Recommended for a licence", summary.RecommendedForLicence,
                $"Unlicensed users whose business-case score reached {summary.Options.OpportunityRecommendScore}.");

            if (summary.CoworkDetected)
            {
                AddMeta(sheet, "Cowork users", summary.CoworkUsers, "Licensed users who used Microsoft 365 Copilot Cowork.");
                AddMeta(sheet, "Cowork adoption %", summary.CoworkAdoptionPct, "Cowork users as a share of licensed users.");
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
                    + "-day month so they mean the same thing whichever period was selected. A seat that was never "
                    + "used is not 'infrequent' - it is in the reclaimable-seat figure."));
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
                + "period cannot show. Weeks start on a Monday, in UTC. A week with no data is written as zero "
                + "rather than skipped, so a gap in the import is visible instead of being smoothed over."));
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
                    row.Add(point?.Value ?? 0d);
                }
                sheet.AddRow(row.ToArray());
            }

            var last = sheet.CurrentRow;
            var headerRow = first - 1;

            // People and interaction volumes are charted separately: a few hundred users plotted against
            // tens of thousands of interactions flattens the user line onto zero.
            AddTrendChart(sheet, summary.WeeklyTrend, allSeries, first, last, headerRow,
                "Weekly active users", "N3", XlsxChartType.Line);

            AddTrendChart(sheet, summary.WeeklyVolumeTrend, allSeries, first, last, headerRow,
                "Weekly Copilot volume", "N22", XlsxChartType.StackedArea);

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
                    + $"Departments with fewer than {summary.Options.MinSeatsPerSegment} seats are omitted because "
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
                    "Licensed and unlicensed side by side. A department with idle seats AND heavy unlicensed use "
                    + "is a seat-allocation problem, not an adoption problem, and can usually be fixed at no cost. "
                    + "Interactions per seat divides by all seats including idle ones - that is the point of the "
                    + "comparison. Both per-user columns are normalised to a month."));
                sheet.AddHeaderRow("Department", "Seats", "Active seats", "Interactions per seat",
                    "Seats using agents %", "Unlicensed users", "Interactions per unlicensed user",
                    "Unlicensed using agents %");

                foreach (var row in summary.CombinedByDepartment)
                {
                    sheet.AddRow(row.Segment, row.LicensedUsers, row.LicensedActiveUsers,
                        row.InteractionsPerLicensedUser, row.LicensedAgentUserPct, row.UnlicensedActiveUsers,
                        row.InteractionsPerUnlicensedUser, row.UnlicensedAgentUserPct);
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
                    "What Copilot grounded its answers in. The clearest evidence available that Copilot is doing "
                    + "work on your own content rather than answering generic questions any free chatbot could."));
                sheet.AddHeaderRow("Resource type", "References");
                foreach (var type in summary.TopResourceTypes)
                {
                    sheet.AddRow(type.Label, type.Value);
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
                "People with no Copilot seat who use Copilot anyway. This is the one Copilot population "
                + "Microsoft's own reporting cannot see at all, and the strongest evidence of unmet demand "
                + "available - these people chose to use Copilot with no seat, no training and no prompting."));
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
            sheet.SetColumnWidths(26, 12, 14, 78);

            sheet.AddTitle("Enablement plan");
            sheet.AddRow(XlsxCell.Wrapped(
                "Every licensed user needs exactly one of these next steps, so the counts add up to the whole "
                + "scored population. This is the size of each job."));
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Action", "Users", "% of licensed", "What it means and why these users qualify");

            var first = sheet.CurrentRow + 1;
            foreach (var action in summary.ActionPlan)
            {
                sheet.AddRow(action.Label, action.Users, action.SharePct, XlsxCell.Wrapped(action.Description));
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
            sheet.SetColumnWidths(34, 26, 22, 22, 12, 10, 14, 12, 10, 10, 14, 14, 22, 60);

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

            sheet.AddHeaderRow(
                "User", "Department", "Job title", "Manager", "Engagement", "Band", "Interactions",
                "Active days", "Expected", "Apps", "Used Cowork", "Days since last use", "Recommended action",
                "Action detail");

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
                    user.Interactions,
                    user.ActiveDays,
                    user.ExpectedActiveDays,
                    user.AppsUsed,
                    user.UsedCowork ? "Yes" : "No",
                    user.DaysSinceLastUse.HasValue ? (object)user.DaysSinceLastUse.Value : "-",
                    user.RecommendedActionLabel,
                    XlsxCell.Wrapped(user.RecommendedAction));
            }

            sheet.FreezeTopRows(headerRow);
            sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 14);
        }

        private static void WriteOpportunitiesSheet(XlsxWriter workbook, CopilotAdoptionAnalysis analysis)
        {
            var candidates = analysis.Opportunities ?? new List<LicenceOpportunityRow>();
            if (candidates.Count == 0) return;

            var sheet = workbook.AddSheet("Licence opportunities");
            sheet.SetColumnWidths(34, 26, 22, 14, 14, 22, 14, 14, 14, 60);

            if (candidates.Count > MaxUserRows)
            {
                sheet.AddTitle("Licence opportunities - TRUNCATED");
                sheet.AddRow(XlsxCell.Wrapped(
                    $"This sheet lists the {MaxUserRows:N0} strongest of {candidates.Count:N0} candidates. "
                    + "The headline 'recommended for a licence' figure covers all of them."));
                sheet.AddBlankRow();
            }

            sheet.AddHeaderRow(
                "User", "Department", "Job title", "Business case", "Recommended",
                "Unlicensed Copilot interactions", "Teams", "Email", "Files", "Justification");

            var headerRow = sheet.CurrentRow;

            foreach (var candidate in candidates.OrderByDescending(c => c.OpportunityScore).Take(MaxUserRows))
            {
                sheet.AddRow(
                    candidate.UserPrincipalName,
                    candidate.Department ?? string.Empty,
                    candidate.JobTitle ?? string.Empty,
                    candidate.OpportunityScore,
                    candidate.Recommended ? "Yes" : "No",
                    candidate.UnlicensedCopilotInteractions,
                    candidate.TeamsMessages + candidate.TeamsMeetings,
                    candidate.EmailsSent + candidate.EmailsRead,
                    candidate.FilesViewedOrEdited,
                    XlsxCell.Wrapped(candidate.Rationale));
            }

            sheet.FreezeTopRows(headerRow);
            sheet.AddAutoFilter(headerRow, sheet.CurrentRow, 1, 10);
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
            var targetDays = Math.Round(o.WindowDays * (o.WorkingDaysPerWeek / 7d) * o.FrequencyTargetRatio, 0);
            var weightSum = o.FrequencyWeight + o.DepthWeight + o.BreadthWeight;

            sheet.AddTitle("How this is calculated");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Measure", "Definition");

            AddMethod(sheet, "Engagement score",
                "Each licensed user scores 0-100 from three capped components, because 'did they use Copilot?' is "
                + "almost never a yes/no question - someone who opened it twice and someone who lives in it produce "
                + "the same 'active user' count and need opposite responses.\n"
                + $"frequency = min(1, activeDays / {targetDays})\n"
                + $"depth = min(1, (interactions / activeDays) / {o.DepthTargetInteractionsPerActiveDay})\n"
                + $"breadth = min(1, appsUsed / {o.BreadthTargetApps})\n"
                + $"score = (frequency x {o.FrequencyWeight} + depth x {o.DepthWeight} + breadth x {o.BreadthWeight}) / {weightSum} x 100");

            AddMethod(sheet, "Why working days",
                $"The frequency target is {o.FrequencyTargetRatio:P0} of the working days in the period, assuming "
                + $"{o.WorkingDaysPerWeek} working days a week - {targetDays} days over {o.WindowDays}. Measured "
                + "against calendar days, someone who used Copilot every single working day would cap out at about "
                + "71% and look like a partial adopter.");

            AddMethod(sheet, "Bands",
                $"Champion at {o.ChampionScore}+, Established at {o.EstablishedScore}+, Developing at "
                + $"{o.DevelopingScore}+, Trialling below that. Users with no activity in the period are not scored "
                + $"at all: Dormant means they used Copilot within the last {o.HistoryDays} days but not in this "
                + "period; Never used means no activity anywhere in that history. The distinction decides the "
                + "action - one needs a conversation, the other needs onboarding or the seat back.");

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
                + "Percentages are of ACTIVE users. A seat that was never used is not 'infrequent' - it is a "
                + "reclaimable seat, and merging the two hides the more expensive problem.");

            AddMethod(sheet, "Usage concentration",
                "Active licensed users ranked by interaction count and cut into percentile cohorts. Only active "
                + "users are ranked - including idle seats would put every one of them in the bottom cohort at zero "
                + "and give every tenant an identical chart.");

            AddMethod(sheet, "Business case score",
                "Unlicensed users score 0-100 on four weighted signals, weighted so evidence beats inference:\n"
                + $"copilot = min(1, unlicensedCopilotInteractions / {o.OpportunityCopilotTarget}) x {o.OpportunityUnlicensedCopilotWeight}\n"
                + $"collaboration = min(1, (teamsMessages + teamsMeetings) / {o.OpportunityCollaborationTarget}) x {o.OpportunityCollaborationWeight}\n"
                + $"email = min(1, (emailsSent + emailsRead) / {o.OpportunityEmailTarget}) x {o.OpportunityEmailWeight}\n"
                + $"documents = min(1, filesViewedOrEdited / {o.OpportunityDocumentTarget}) x {o.OpportunityDocumentWeight}\n"
                + $"Recommended at {o.OpportunityRecommendScore} or above. Already using Copilot Chat without a seat "
                + "carries the most weight because it is the only signal that proves demand for Copilot itself "
                + "rather than inferring it from general activity.");

            AddMethod(sheet, "Agent verdicts",
                $"Retire after {o.AgentRetireInactiveDays} days without use; Review between {o.AgentReviewInactiveDays} "
                + $"and {o.AgentRetireInactiveDays} days, or while current but used by fewer than {o.AgentMinUsers} "
                + $"people; Keep when used within {o.AgentReviewInactiveDays} days by at least {o.AgentMinUsers} "
                + $"people. Any agent first seen within {o.AgentNewDays} days is New and exempt from review - a "
                + "brand-new agent with two users has not failed, it has not started.");

            AddMethod(sheet, "Comparing two snapshots",
                "These figures are only comparable between two runs if both used the same thresholds and the same "
                + "period length. Both are recorded on the Report sheet - check them before subtracting one file "
                + "from another, because the tuning is adjustable and 'adoption went up' must not turn out to mean "
                + "'the bar moved'.");

            AddMethod(sheet, "Licence classification",
                "Microsoft ships Copilot-branded SKUs that are not a Microsoft 365 Copilot seat (Copilot Studio, "
                + "Copilot for Sales), and ships new seat SKUs regularly. Everything found is listed below, "
                + "including what was excluded, so the licensed population can be checked rather than trusted.");

            if (summary.SeatLicenceTypes.Count > 0)
            {
                sheet.AddBlankRow();
                sheet.AddHeaderRow("Product", "SKU", "Users", "Counted as a Copilot seat");
                foreach (var licence in summary.SeatLicenceTypes)
                {
                    sheet.AddRow(licence.Name, licence.SkuPartNumber, licence.AssignedUsers,
                        licence.IsCopilotSeat ? "Yes" : "No");
                }
            }

            sheet.FreezeTopRows(3);
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

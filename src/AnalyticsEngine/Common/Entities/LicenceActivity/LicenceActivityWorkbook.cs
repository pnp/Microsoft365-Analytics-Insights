using Common.Entities.Xlsx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.LicenceActivity
{
    public static class LicenceActivityWorkbook
    {
        public static byte[] Build(LicenceActivityOverview overview, LicenceActivityUsers users = null)
        {
            if (overview == null) throw new ArgumentNullException(nameof(overview));
            if (users != null && users.OverviewId != overview.SnapshotId)
                throw new ArgumentException("The user snapshot does not belong to this overview.", nameof(users));

            using (var book = new XlsxWriter())
            {
                var report = book.AddSheet("Summary").SetColumnWidths(32, 40, 100);
                report.AddTitle("Microsoft 365 licence activity");
                report.AddHeaderRow("Property", "Value", "Meaning");
                report.AddRow("Report prepared (UTC)", overview.GeneratedUtc.ToString("O"), "The figures shown on screen; exporting does not re-run the report.");
                report.AddRow("From (UTC)", overview.Query.From, "First day included.");
                report.AddRow("To (UTC)", overview.Query.To, "Last day included; the dates each source represents are on the \"Where the figures come from\" sheet.");
                report.AddRow("Department filter", overview.Query.DepartmentId, "Blank = all departments; 0 = people with no department recorded.");
                report.AddRow("Country filter", overview.Query.CountryId, "Blank = all countries; 0 = people with no country recorded.");
                report.AddRow("People holding a licence", overview.DistinctAssignedUsers, "Each person counted once across the selected population.");
                report.AddRow("Department/country lists capped", overview.DemographicsTruncated, "TRUE means only the largest departments and countries are listed.");
                report.AddRow("Who holds each licence", null, XlsxCell.Wrapped(LicenceActivityRules.AssignmentCaveat));
                report.AddRow("How to read this", null, XlsxCell.Wrapped(LicenceActivityRules.InterpretationCaveat));
                report.AddRow("Activity levels", null, XlsxCell.Wrapped(LicenceActivityRules.Method));
                foreach (var message in overview.Messages) report.AddRow("Note", null, XlsxCell.Wrapped(message));

                var licences = book.AddSheet("Licences");
                licences.AddHeaderRow("Licence", "Licence code", "People assigned", "Service", "High", "Moderate", "Low", "No activity", "Unknown");
                foreach (var sku in overview.Licences)
                    foreach (var distribution in sku.Workloads)
                        licences.AddRow(sku.Name, sku.SkuId, sku.AssignedUsers, LicenceActivityRules.WorkloadLabel(distribution.Workload),
                            distribution.High, distribution.Moderate, distribution.Low, distribution.Zero, distribution.Unknown);
                FormatTable(licences, 9);
                report.AddRow("Licences sheet", null, "One row per licence and service. The \"People assigned\" figure repeats on each of that licence's rows, so do not add that column up.");

                var coverage = book.AddSheet("Where the figures come from");
                coverage.AddHeaderRow("Service", "Data", "Source", "What was measured", "How often it was measured", "Data from (UTC)",
                    "Data to (UTC)", "Last imported (UTC)", "Days old", "Days each report covers", "Measurements taken",
                    "Measurements expected", "People who couldn't be matched", "Dates measured (UTC)", "Notes");
                foreach (var source in overview.Coverage)
                    coverage.AddRow(LicenceActivityRules.WorkloadLabel(source.Workload),
                        LicenceActivityRules.StatusLabel(source.Status), LicenceActivityRules.SourceLabel(source.Source),
                        source.Measure, LicenceActivityRules.GranularityLabel(source.Granularity),
                        source.EffectiveFromUtc, source.EffectiveToUtc, source.LatestImportUtc, source.LagDays,
                        source.ReportPeriodDays, source.ObservedSamples, source.ExpectedSamples, source.UnmatchedUsers,
                        string.Join(", ", source.SnapshotDates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
                        XlsxCell.Wrapped(source.Message));
                FormatTable(coverage, 15);

                WriteDemographics(book, "Departments", overview.Departments);
                WriteDemographics(book, "Countries", overview.Countries);
                if (users != null)
                {
                    if (users.MostActive.Count > LicenceActivityQuery.MaximumRows
                        || users.LeastActive.Count > LicenceActivityQuery.MaximumRows
                        || users.Users.Count > LicenceActivityQuery.MaximumRows)
                        throw new ArgumentException("User snapshot exceeds the export row limit.", nameof(users));
                    report.AddRow("People list prepared (UTC)", users.GeneratedUtc.ToString("O"), "The lists and page currently on screen, not everyone holding the licence.");
                    report.AddRow("Selected licence ID", users.Query.LicenceTypeId);
                    report.AddRow("Ranked by service", LicenceActivityRules.WorkloadLabel(users.Query.Workload), "Services are never combined into a single score.");
                    report.AddRow("Size of each list", users.Query.Top);
                    report.AddRow("Search", users.Query.Search);
                    report.AddRow("Sorted by", SortLabel(users.Query.Sort, users.Query.Direction));
                    report.AddRow("Page", users.Query.Page);
                    report.AddRow("People per page", users.Query.PageSize);
                    report.AddRow("People matching", users.TotalUsers);
                    report.AddRow("People who can be ranked", users.RankedUsers, "Anyone without enough evidence is left out of the least-active list.");
                    foreach (var message in users.Messages) report.AddRow("Note about people", null, XlsxCell.Wrapped(message));
                    WriteUsers(book, "Most active", users.MostActive);
                    WriteUsers(book, "Least active", users.LeastActive);
                    WriteUsers(book, "People page", users.Users);
                }
                else report.AddRow("Individual people", "Not included", "Totals only.");
                return book.ToArray();
            }
        }

        /// <summary>The browse ordering, worded as the drop-down on screen words it.</summary>
        private static string SortLabel(string sort, string direction)
        {
            var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
            switch (sort)
            {
                case "activity": return descending ? "Most active first" : "Least active first";
                case "lastActivity": return descending ? "Most recently active" : "Longest since active";
                case "upn": return descending ? "Sign-in address (Z-A)" : "Sign-in address (A-Z)";
                default: return sort + " " + direction;
            }
        }

        private static void WriteDemographics(XlsxWriter book, string name, IEnumerable<LicenceActivityDemographic> groups)
        {
            var sheet = book.AddSheet(name);
            sheet.AddHeaderRow("Group ID", "Group", "People assigned", "Service", "High", "Moderate", "Low", "No activity", "Unknown");
            foreach (var group in groups)
                foreach (var distribution in group.Workloads)
                    sheet.AddRow(group.Id, group.Name, group.AssignedUsers, LicenceActivityRules.WorkloadLabel(distribution.Workload),
                        distribution.High, distribution.Moderate, distribution.Low, distribution.Zero, distribution.Unknown);
            FormatTable(sheet, 9);
        }

        private static void WriteUsers(XlsxWriter book, string name, IEnumerable<LicenceActivityUser> users)
        {
            var sheet = book.AddSheet(name);
            sheet.AddHeaderRow("Person ID", "Sign-in address", "Department", "Country", "Account enabled",
                "Service", "Data", "Activity", "Source", "What was measured", "Times active", "Measurements taken",
                "Measurements expected", "Average actions", "Last active (UTC)");
            foreach (var user in users)
                foreach (var evidence in user.Workloads)
                    sheet.AddRow(user.UserId, user.UserPrincipalName, user.Department, user.Country, user.AccountEnabled,
                        LicenceActivityRules.WorkloadLabel(evidence.Workload), LicenceActivityRules.StatusLabel(evidence.Status),
                        LicenceActivityRules.BandLabel(evidence.Band), LicenceActivityRules.SourceLabel(evidence.Source),
                        evidence.Measure, evidence.ActiveSamples,
                        evidence.ObservedSamples, evidence.ExpectedSamples, evidence.AverageActions, evidence.LastActivityUtc);
            FormatTable(sheet, 15);
        }

        private static void FormatTable(XlsxSheet sheet, int columns)
        {
            sheet.FreezeTopRows(1);
            sheet.AddAutoFilter(1, sheet.CurrentRow, 1, columns);
        }
    }
}

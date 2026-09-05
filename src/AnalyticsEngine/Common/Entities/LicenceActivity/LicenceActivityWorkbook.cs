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
                var report = book.AddSheet("Snapshot").SetColumnWidths(32, 40, 100);
                report.AddTitle("Microsoft 365 licence activity");
                report.AddHeaderRow("Property", "Value", "Meaning");
                report.AddRow("Overview generated (UTC)", overview.GeneratedUtc.ToString("O"), "The values displayed on screen; export does not re-query the database.");
                report.AddRow("Requested from (UTC)", overview.Query.From, "Inclusive UTC date.");
                report.AddRow("Requested to (UTC)", overview.Query.To, "Inclusive UTC date; actual source coverage is on the Workload coverage sheet.");
                report.AddRow("Department filter ID", overview.Query.DepartmentId, "Blank = all; 0 = unknown metadata.");
                report.AddRow("Country filter ID", overview.Query.CountryId, "Blank = all; 0 = unknown metadata.");
                report.AddRow("Distinct assigned users", overview.DistinctAssignedUsers, "Unique people across the selected demographic population.");
                report.AddRow("Demographics truncated", overview.DemographicsTruncated, "Only the largest demographic groups are displayed when the bound is reached.");
                report.AddRow("Current assignments", null, XlsxCell.Wrapped(LicenceActivityRules.AssignmentCaveat));
                report.AddRow("Interpretation", null, XlsxCell.Wrapped(LicenceActivityRules.InterpretationCaveat));
                report.AddRow("Activity bands", null, XlsxCell.Wrapped(LicenceActivityRules.Method));
                foreach (var message in overview.Messages) report.AddRow("Note", null, XlsxCell.Wrapped(message));

                var licences = book.AddSheet("Licences");
                licences.AddHeaderRow("Licence", "SKU ID", "Assigned users", "Workload", "High", "Moderate", "Low", "No activity", "Unknown");
                foreach (var sku in overview.Licences)
                    foreach (var distribution in sku.Workloads)
                        licences.AddRow(sku.Name, sku.SkuId, sku.AssignedUsers, distribution.Workload,
                            distribution.High, distribution.Moderate, distribution.Low, distribution.Zero, distribution.Unknown);
                FormatTable(licences, 9);
                report.AddRow("Licence sheet", null, "One row per SKU and workload. Assigned users repeat across workloads; do not sum this column.");

                var coverage = book.AddSheet("Workload coverage");
                coverage.AddHeaderRow("Workload", "Status", "Source", "Measure", "Granularity", "Effective from (UTC)",
                    "Effective through (UTC)", "Latest import (UTC)", "Lag days", "Report period days", "Observed samples",
                    "Expected samples", "Unmatched users", "Snapshot dates (UTC)", "Notes");
                foreach (var source in overview.Coverage)
                    coverage.AddRow(source.Workload, source.Status, source.Source, source.Measure, source.Granularity,
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
                    report.AddRow("Users generated (UTC)", users.GeneratedUtc.ToString("O"), "Current bounded lists and browse page, not the entire assigned population.");
                    report.AddRow("Selected licence ID", users.Query.LicenceTypeId);
                    report.AddRow("Ranking workload", users.Query.Workload, "Workloads are not combined into a score.");
                    report.AddRow("Most/least count", users.Query.Top);
                    report.AddRow("Search", users.Query.Search);
                    report.AddRow("Browse sort", users.Query.Sort + " " + users.Query.Direction);
                    report.AddRow("Browse page", users.Query.Page);
                    report.AddRow("Page size", users.Query.PageSize);
                    report.AddRow("Matching users", users.TotalUsers);
                    report.AddRow("Users with rankable evidence", users.RankedUsers, "Missing evidence is excluded from least-active ranking.");
                    foreach (var message in users.Messages) report.AddRow("User note", null, XlsxCell.Wrapped(message));
                    WriteUsers(book, "Most active", users.MostActive);
                    WriteUsers(book, "Least active", users.LeastActive);
                    WriteUsers(book, "User page", users.Users);
                }
                else report.AddRow("Individual users", "Not included", "Aggregate-only snapshot.");
                return book.ToArray();
            }
        }

        private static void WriteDemographics(XlsxWriter book, string name, IEnumerable<LicenceActivityDemographic> groups)
        {
            var sheet = book.AddSheet(name);
            sheet.AddHeaderRow("Group ID", "Group", "Distinct assigned users", "Workload", "High", "Moderate", "Low", "No activity", "Unknown");
            foreach (var group in groups)
                foreach (var distribution in group.Workloads)
                    sheet.AddRow(group.Id, group.Name, group.AssignedUsers, distribution.Workload,
                        distribution.High, distribution.Moderate, distribution.Low, distribution.Zero, distribution.Unknown);
            FormatTable(sheet, 9);
        }

        private static void WriteUsers(XlsxWriter book, string name, IEnumerable<LicenceActivityUser> users)
        {
            var sheet = book.AddSheet(name);
            sheet.AddHeaderRow("User ID", "UPN", "Department", "Country", "Account enabled",
                "Workload", "Coverage", "Band", "Source", "Measure", "Active samples", "Observed samples", "Expected samples",
                "Average actions per observed sample", "Last activity (UTC)");
            foreach (var user in users)
                foreach (var evidence in user.Workloads)
                    sheet.AddRow(user.UserId, user.UserPrincipalName, user.Department, user.Country, user.AccountEnabled,
                        evidence.Workload, evidence.Status, evidence.Band, evidence.Source, evidence.Measure, evidence.ActiveSamples,
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

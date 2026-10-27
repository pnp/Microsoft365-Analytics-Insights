using Common.Entities.CopilotInvestment;
using Common.Entities.Localisation;
using Common.Entities.Xlsx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotSpend
{
    /// <summary>
    /// The Copilot Spend report as an Excel workbook, in the reader's language: the same scope, period and source split as the
    /// page, numbers and dates as typed cells, and tenant text (names, departments, agents, categories) exactly as stored.
    /// </summary>
    /// <remarks>
    /// Text cells are inline strings, which Excel never evaluates, so a value that starts with <c>=</c> stays text. The people
    /// sheet is written only for a reader with See PII; without it the workbook carries aggregates only, like the page. Every
    /// list is written in full - nothing is paged away.
    /// </remarks>
    public static class CopilotSpendWorkbook
    {
        public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        /// <summary>The catalog module the workbook's wording lives in.</summary>
        public const string CatalogModule = "copilotSpend";

        public static string FileName(CopilotSpendSummary summary, PortalLanguage language = PortalLanguage.English)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            return new Words(language).W("fileName",
                ("date", summary.GeneratedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("days", summary.WindowDays.ToString(CultureInfo.InvariantCulture)));
        }

        public static byte[] Build(CopilotSpendSummary summary, bool includeIndividualData, string buildLabel,
            PortalLanguage language = PortalLanguage.English)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            var words = new Words(language);
            using (var workbook = new XlsxWriter())
            {
                WriteSummary(workbook, summary, includeIndividualData, buildLabel, words);
                WriteOrganisation(workbook, summary, words);
                WriteStudio(workbook, summary, words);
                WriteBalances(workbook, summary, words);
                if (includeIndividualData) WritePeople(workbook, summary, words);
                return workbook.ToArray();
            }
        }

        private static void WriteSummary(XlsxWriter workbook, CopilotSpendSummary summary, bool includeIndividualData, string buildLabel, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.summary"));
            sheet.SetColumnWidths(48, 20, 14, 80);
            sheet.AddTitle(words.W("summary.title"));
            sheet.AddRow(words.W("summary.period"), XlsxCell.Date(summary.FromUtc), XlsxCell.Date(summary.ToUtc),
                words.W("unit.daysCount", ("days", words.Count(summary.WindowDays))));
            sheet.AddRow(words.W("summary.generated"), summary.GeneratedUtc, null, buildLabel);
            sheet.AddRow(words.W("summary.covers"), null, null, XlsxCell.Wrapped(Coverage(summary, words)));
            sheet.AddRow(words.W("summary.personData"), includeIndividualData ? words.W("summary.personDataIncluded") : words.W("summary.personDataExcluded"));
            sheet.AddRow(words.W("summary.sources"), null, null, XlsxCell.Wrapped(words.W("summary.sourcesNote")));
            sheet.AddBlankRow();

            var credits = words.W("unit.credits");
            var people = words.W("unit.people");
            var studio = summary.Studio;
            sheet.AddHeaderRow(words.W("summary.col.studio"), words.W("col.value"), words.W("col.unit"), words.W("col.note"));
            // A source that is not available leaves its cells empty: never a zero the source did not report.
            sheet.AddRow(words.W("studio.credits"), studio.Credits, credits,
                !studio.Available ? words.W("studio.notAvailable")
                : studio.UnmatchedCredits > 0 ? words.W("studio.creditsUnmatched", ("credits", words.Num(studio.UnmatchedCredits))) : words.W("studio.creditsNote"));
            sheet.AddRow(words.W("studio.people"), Count(studio.People), people);
            sheet.AddRow(words.W("studio.latest"), studio.LatestDate, null, words.W("studio.latestNote"));
            sheet.AddRow(words.W("studio.earliest"), studio.EarliestDate, null,
                studio.EarliestDate.HasValue && !studio.CoversWindow ? words.W("studio.partialWindow") : null);
            sheet.AddRow(words.W("studio.lastImport"), studio.LastImportUtc, null, studio.LastImportFailed ? words.W("studio.lastImportFailed") : null);
            if (!studio.ImportEnabled) sheet.AddRow(words.W("studio.disabled"));
            if (studio.FailureCode != null) sheet.AddRow(words.W("studio.unavailable"));
            sheet.AddBlankRow();

            var balances = summary.Balances;
            sheet.AddHeaderRow(words.W("summary.col.balances"), words.W("col.value"), words.W("col.unit"), words.W("col.note"));
            sheet.AddRow(words.W("balances.credits"), balances.Credits, credits,
                balances.Available ? words.W("balances.creditsNote") : words.W("balances.notAvailable"));
            sheet.AddRow(words.W("balances.snapshot"), balances.SnapshotDate, null,
                words.W("balances.snapshotNote", ("days", words.Count(CopilotInvestmentSql.SnapshotBandDays))));
            if (balances.SnapshotBeforeWindow)
                sheet.AddRow(XlsxCell.Wrapped(words.W("balances.snapshotBeforeWindow", ("date", words.Date(balances.SnapshotDate)))));
            sheet.AddRow(words.W("balances.periodStatus"), balances.PeriodStatus == null ? words.W("balances.periodStatus.none")
                : words.Shared("copilotInvestment.quality.period." + balances.PeriodStatus, balances.PeriodStatus));
            sheet.AddRow(words.W("balances.observed"), XlsxCell.Number(balances.ObservedPeople), people);
            sheet.AddRow(words.W("balances.consuming"), XlsxCell.Number(balances.ConsumingPeople), people);
            sheet.AddRow(words.W("balances.partial"), XlsxCell.Number(balances.PartialPeople), people);
            sheet.AddRow(words.W("balances.unknown"), XlsxCell.Number(balances.UnknownAmountPeople), people, words.W("balances.unknownNote"));
            sheet.AddRow(words.W("balances.otherUnit"), XlsxCell.Number(balances.OtherUnitPeople), people, words.W("balances.otherUnitNote"));
            sheet.AddRow(words.W("balances.limit"), XlsxCell.Number(balances.LimitExhaustedPeople), people);
            sheet.AddRow(words.W("balances.notEntitled"), XlsxCell.Number(balances.NotEntitledPeople), people);
            if (balances.UnresolvedAccounts.HasValue)
            {
                sheet.AddRow(words.W("balances.unresolvedAccounts"), XlsxCell.Number(balances.UnresolvedAccounts.Value), words.W("unit.accounts"));
                sheet.AddRow(words.W("balances.unresolvedCredits"), balances.UnresolvedCredits, credits, words.W("balances.unresolvedNote"));
            }
            sheet.AddRow(words.W("balances.freshness"), balances.OldestFreshness?.UtcDateTime, balances.NewestFreshness?.UtcDateTime, words.W("balances.freshnessNote"));
            sheet.AddRow(words.W("balances.sweeps"), XlsxCell.Number(balances.SweepsInWindow), null,
                words.W("balances.sweepsCompleted", ("completed", words.Count(balances.CompletedSweepsInWindow))));
            if (!balances.CollectionEnabled) sheet.AddRow(words.W("balances.notCollecting"));
            if (!balances.TablesPresent) sheet.AddRow(words.W("balances.notInstalled"));
            if (balances.FailureCode != null) sheet.AddRow(words.W("balances.unavailable"));
        }

        /// <summary>Who the figures cover: everyone, or the administrator's filter and the reader's own, in the reader's language.</summary>
        private static string Coverage(CopilotSpendSummary summary, Words words)
        {
            if (!summary.Filtered) return words.W("summary.everyone");
            var lines = new List<string>();
            var global = FilterWording.Describe(summary.GlobalFilter, words.Language);
            if (!string.IsNullOrEmpty(global)) lines.Add(words.W("summary.filterAdmin", ("filter", global)));
            var reader = FilterWording.Describe(summary.UserFilter, words.Language);
            if (!string.IsNullOrEmpty(reader)) lines.Add(words.W("summary.filterReader", ("filter", reader)));
            return lines.Count > 0 ? string.Join("\n", lines) : words.W("summary.filteredUnknown");
        }

        private static void WriteOrganisation(XlsxWriter workbook, CopilotSpendSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.organisation"));
            var organisation = summary.Organisation;
            sheet.SetColumnWidths(34, 12, 14, 16, 14, 16, 16, 14, 14, 40);
            sheet.AddRow(words.W("org.groupedBy"), FilterWording.DimensionLabel(organisation.Dimension, organisation.DimensionName, words.Language), null,
                organisation.MinGroupSize > 1 ? words.W("org.minGroup", ("count", words.Count(organisation.MinGroupSize))) : words.W("org.everyGroup"));
            sheet.AddRow(XlsxCell.Wrapped(words.W("org.note")));
            if (organisation.Hidden)
            {
                sheet.AddRow(words.W("org.hidden"));
                return;
            }
            if (organisation.UnavailableCode != null)
            {
                sheet.AddRow(XlsxCell.Wrapped(words.W("org.unavailable." + organisation.UnavailableCode)));
                return;
            }
            sheet.AddHeaderRow(words.W("org.col.group"), words.W("org.col.people"), words.W("org.col.studioPeople"), words.W("org.col.studioCredits"),
                words.W("org.col.shareOfStudio"), words.W("org.col.balancePeople"), words.W("org.col.balanceCredits"), words.W("org.col.shareOfBalances"),
                words.W("org.col.unknown"), words.W("col.note"));
            var first = sheet.CurrentRow;
            // Every group, not the page's top ones: only groups too small to show alone are combined, and that row says so.
            foreach (var g in organisation.AllGroups)
                sheet.AddRow(GroupLabel(g, words), XlsxCell.Number(g.People), Count(g.StudioPeople), g.StudioCredits, Percent(g.ShareOfStudio),
                    Count(g.BalancePeople), g.BalanceCredits, Percent(g.ShareOfBalances), Count(g.UnknownAmountPeople), GroupNote(g, organisation, words));
            sheet.FreezeTopRows(first);
            if (sheet.CurrentRow > first) sheet.AddAutoFilter(first, sheet.CurrentRow, 1, 10);
        }

        private static string GroupLabel(CopilotSpendGroupRow g, Words words) =>
            g.Label ?? (g.EmptyKey == CopilotInvestmentCalculator.GroupKeys.NotSet ? words.W("org.notSet")
                : g.EmptyKey == CopilotSpendGroupKeys.MoreGroups ? words.W("org.moreGroups", ("count", words.Count(g.Folded)))
                : words.W("org.combined"));

        private static string GroupNote(CopilotSpendGroupRow g, CopilotSpendOrganisation organisation, Words words)
        {
            var notes = new List<string>();
            if (g.EmptyKey == CopilotInvestmentCalculator.GroupKeys.OtherSmallGroups)
                notes.Add(words.W("org.combinedNote", ("groups", words.Count(g.Folded)), ("count", words.Count(organisation.MinGroupSize))));
            if (g.Withheld) notes.Add(words.W("org.withheld", ("count", words.Count(organisation.MinGroupSize))));
            if (g.StudioCredits == null && !g.Withheld) notes.Add(words.W("org.studioNotAvailable"));
            if (g.BalanceCredits == null && !g.Withheld) notes.Add(words.W("org.balancesNotAvailable"));
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        private static void WriteStudio(XlsxWriter workbook, CopilotSpendSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.studio"));
            sheet.SetColumnWidths(40, 40, 16, 12, 12);
            var studio = summary.Studio;
            sheet.AddTitle(words.W("studio.title"));
            sheet.AddRow(XlsxCell.Wrapped(words.W("studio.intro")));
            if (!studio.Available)
            {
                sheet.AddRow(XlsxCell.Wrapped(words.W("studio.notAvailable")));
                return;
            }
            if (studio.BreakdownFailureCode != null)
            {
                sheet.AddRow(words.W("studio.breakdownUnavailable"));
                return;
            }
            sheet.AddBlankRow();
            sheet.AddRow(words.W("studio.latest"), studio.LatestDate, null, words.W("studio.latestNote"));
            sheet.AddHeaderRow(words.W("studio.col.date"), words.W("studio.col.credits"), words.W("studio.col.people"), words.W("col.note"));
            // Every calendar day: a day the ledger has no charges for is said to be so, and one after its latest day is not reported.
            foreach (var day in studio.Days)
                sheet.AddRow(XlsxCell.Date(day.Date), day.Credits, Count(day.People),
                    day.Status == CopilotSpendDayStatus.Recorded ? null : words.W("studio.day." + day.Status));
            sheet.AddBlankRow();
            sheet.AddRow(words.W("studio.environmentsTitle"));
            sheet.AddHeaderRow(words.W("studio.col.environment"), words.W("studio.col.environmentId"), words.W("studio.col.credits"), words.W("studio.col.people"),
                words.W("studio.col.days"));
            foreach (var environment in studio.Environments)
                sheet.AddRow(environment.EmptyKey == CopilotSpendGroupKeys.OtherEnvironments
                        ? words.W("studio.otherEnvironments", ("count", words.Count(environment.Folded)))
                        : environment.EnvironmentName ?? words.W("studio.unnamedEnvironment"),
                    environment.Withheld ? words.W("studio.environmentsWithheld") : environment.EnvironmentId, environment.Credits,
                    XlsxCell.Number(environment.People), Count(environment.Days));
            sheet.AddBlankRow();
            sheet.AddRow(words.W("studio.agentsTitle"));
            if (studio.Agents == null)
            {
                // Withheld from a filtered view by design; unfiltered, the agent ledger simply was not there to read.
                sheet.AddRow(XlsxCell.Wrapped(words.W(summary.Filtered ? "studio.agentsWithheld" : "studio.agentsNotAvailable")));
                return;
            }
            sheet.AddRow(XlsxCell.Wrapped(words.W("studio.agentsNote")));
            sheet.AddHeaderRow(words.W("studio.col.agent"), words.W("studio.col.agentId"), words.W("studio.col.credits"), words.W("studio.col.days"));
            foreach (var agent in studio.Agents)
                sheet.AddRow(agent.AgentName ?? words.W("studio.unnamedAgent"), agent.AgentId, agent.Credits, XlsxCell.Number(agent.Days));
            sheet.AddRow(words.W("studio.agentsTotal"), null, studio.AgentLedgerCredits);
        }

        private static void WriteBalances(XlsxWriter workbook, CopilotSpendSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.balances"));
            sheet.SetColumnWidths(36, 18, 14, 16, 14, 14, 14, 14, 40);
            var balances = summary.Balances;
            sheet.AddTitle(words.W("balances.title"));
            sheet.AddRow(XlsxCell.Wrapped(words.W("balances.intro")));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("balances.col.service"), words.W("col.unit"), words.W("balances.col.consuming"), words.W("balances.col.quantity"),
                words.W("balances.col.unknown"), words.W("balances.col.limit"), words.W("balances.col.notEntitled"), words.W("balances.col.unevaluated"),
                words.W("balances.col.policies"));
            foreach (var s in balances.Services)
                sheet.AddRow(s.Category, s.Unit ?? words.W("balances.unknownUnit"), XlsxCell.Number(s.Consumers), s.Quantity, XlsxCell.Number(s.UnknownAmountUsers),
                    XlsxCell.Number(s.LimitExhaustedUsers), XlsxCell.Number(s.NotEntitledUsers), XlsxCell.Number(s.UnevaluatedUsers), string.Join(", ", s.Policies));
            sheet.AddBlankRow();
            if (summary.Filtered)
            {
                sheet.AddRow(words.W("balances.tenantWithheld"));
                return;
            }
            sheet.AddHeaderRow(words.W("balances.col.policy"), words.W("balances.col.scope"), words.W("balances.col.status"), words.W("balances.col.billing"),
                words.W("balances.col.periodLimit"), words.W("balances.col.perUserLimit"), words.W("balances.col.groups"), words.W("balances.col.services"));
            foreach (var p in balances.Policies)
                sheet.AddRow(p.DisplayName ?? p.Id, PolicyScope(p.Type, words),
                    p.Status == null ? words.Shared("copilotInvestment.value.unknown", null) : words.Shared("copilotInvestment.policies.status." + p.Status, p.Status),
                    string.Join(" \u00b7 ", (p.BillingMethods ?? string.Empty).Split(',').Select(m => m.Trim()).Where(m => m.Length > 0)
                        .Select(m => words.Shared("copilotInvestment.policies.billing." + m, m))),
                    Quantity(p.PeriodLimit, words), Quantity(p.PerUserLimit, words), p.AssignedGroups,
                    string.Join(", ", p.Services.Where(s => s.State == "enabled").Select(s => s.ServiceId)));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("balances.col.tenantBalance"), words.W("balances.col.purchased"), words.W("balances.col.consumed"),
                words.W("balances.col.remaining"), words.W("col.unit"), words.W("balances.col.lastModified"));
            foreach (var t in balances.TenantBalances)
                sheet.AddRow(words.Shared("copilotInvestment.policies.billing." + t.BillingMethod, t.BillingMethod), t.Purchased, t.Consumed, t.Remaining, t.Unit,
                    t.LastModified?.UtcDateTime);
        }

        private static string PolicyScope(string type, Words words) =>
            words.Shared(type == "allUsers" ? "copilotInvestment.policies.scope.allUsers"
                : type == "selectedGroups" ? "copilotInvestment.policies.scope.selectedGroupsUnknown"
                : "copilotInvestment.policies.scope.other", type);

        private static string Quantity(CopilotInvestmentQuantity quantity, Words words)
        {
            if (quantity != null && quantity.Kind == CopilotInvestmentQuantityKinds.Unlimited) return words.Shared("copilotInvestment.value.unlimited", quantity.Kind);
            if (quantity != null && quantity.Kind == CopilotInvestmentQuantityKinds.Limited && quantity.Value.HasValue)
                return (words.Num(quantity.Value.Value, 6) + " " + (quantity.Unit ?? string.Empty)).TrimEnd();
            return words.Shared("copilotInvestment.value.unknown", null);
        }

        private static void WritePeople(XlsxWriter workbook, CopilotSpendSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.people"));
            var headers = new[]
            {
                words.W("people.col.upn"), words.W("people.col.department"), words.W("people.col.jobTitle"), words.W("people.col.company"),
                words.W("people.col.country"), words.W("people.col.manager"), words.W("people.col.studioCredits"), words.W("people.col.studioDays"),
                words.W("people.col.balance"), words.W("people.col.balanceDate"), words.W("people.col.partial"),
                words.W("people.col.unknown"), words.W("people.col.otherUnits"), words.W("people.col.limit"), words.W("people.col.notEntitled"),
            };
            sheet.SetColumnWidths(headers.Select(h => (double)Math.Max(12, Math.Min(30, h.Length + 4))).ToArray());
            sheet.AddHeaderRow(headers);
            foreach (var p in CopilotSpendPeople.Sort(summary.People, "user", false))
                sheet.AddRow(p.UserPrincipalName, p.Department, p.JobTitle, p.Company, p.Country, p.Manager, p.StudioCredits, Count(p.StudioDays),
                    p.BalanceCredits, p.BalanceDate.HasValue ? (object)XlsxCell.Date(p.BalanceDate.Value) : null,
                    p.BalanceDate.HasValue ? (object)p.BalancePartial : null, p.BalanceDate.HasValue ? (object)p.UnknownAmounts : null,
                    p.BalanceDate.HasValue ? (object)p.OtherUnits : null, p.BalanceDate.HasValue ? (object)p.LimitExhausted : null,
                    p.BalanceDate.HasValue ? (object)p.NotEntitled : null);
            sheet.FreezeTopRows(1);
            if (sheet.CurrentRow > 1) sheet.AddAutoFilter(1, sheet.CurrentRow, 1, headers.Length);
        }

        /// <summary>The workbook's words in one language: this report's catalog, the shared labels it reuses, numbers as the page writes them.</summary>
        internal sealed class Words
        {
            private const string Prefix = "copilotSpend.workbook.";
            private readonly PortalCatalog _text;
            private readonly PortalCatalog _shared;

            internal Words(PortalLanguage language)
            {
                Language = language;
                _text = PortalCatalog.For(CatalogModule, language);
                _shared = PortalCatalog.For(CopilotInvestmentWorkbook.CatalogModule, language);
                Culture = PortalLanguages.Culture(language);
            }

            internal PortalLanguage Language { get; }

            internal CultureInfo Culture { get; }

            internal string W(string key, params (string Name, string Value)[] values) => _text.Text(Prefix + key, values);

            /// <summary>A label the investment page also shows (policy status, billing method), or the source's own code for one this build does not know.</summary>
            internal string Shared(string key, string fallback) => _shared.Has(key) ? _shared.Text(key) : fallback;

            internal string Num(double? value, int decimals = 2)
            {
                if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "-";
                var text = value.Value.ToString(decimals > 0 ? "#,0." + new string('#', decimals) : "#,0", Culture);
                // Spanish does not group a four-digit number, as the page writes it.
                if (Language == PortalLanguage.Spanish && Math.Abs(value.Value) < 10000)
                    text = text.Replace(Culture.NumberFormat.NumberGroupSeparator, string.Empty);
                return text;
            }

            internal string Count(double? value) => Num(value, 0);

            /// <summary>A date inside a sentence, as the page writes it in this language (26 Oct 2026 / 26 oct 2026).</summary>
            internal string Date(DateTime? value) =>
                value.HasValue ? value.Value.ToString("d MMM yyyy", Culture).Replace(".", string.Empty) : "-";
        }

        /// <summary>A typed number cell, or an empty cell for a figure that is not available - never a zero the source did not report.</summary>
        private static object Count(int? value) => value.HasValue ? (object)XlsxCell.Number(value.Value) : null;

        private static object Percent(double? value) => value.HasValue ? (object)XlsxCell.Percent(value.Value) : null;
    }
}

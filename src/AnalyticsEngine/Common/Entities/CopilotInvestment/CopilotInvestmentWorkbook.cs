using Common.Entities.Localisation;
using Common.Entities.Xlsx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// The Copilot investment report as an Excel workbook: the same summary the page shows, typed (numbers stay
    /// numbers, dates stay dates), with the assumptions, formulas and data quality beside the figures.
    /// </summary>
    /// <remarks>
    /// <para>Text cells are inline strings, which Excel never evaluates, so a tenant value that starts with
    /// <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> stays text. Per-person sheets are written only when the caller
    /// holds See PII; without it the workbook carries aggregates only, exactly like the page.</para>
    /// <para>Every word the product writes comes from the portal's own catalog (<see cref="PortalCatalog"/>), in the
    /// reader's language: English or Spanish. Tenant text - names, departments, policy names, service categories -
    /// is written exactly as stored, in either language.</para>
    /// </remarks>
    public static partial class CopilotInvestmentWorkbook
    {
        public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        /// <summary>The catalog module the workbook's wording lives in.</summary>
        public const string CatalogModule = "copilotInvestment";

        public static string FileName(CopilotInvestmentSummary summary, PortalLanguage language = PortalLanguage.English)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            return new Words(language).W("fileName",
                ("date", summary.GeneratedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("days", summary.WindowDays.ToString(CultureInfo.InvariantCulture)));
        }

        /// <param name="language">The reader's language; English for a caller that does not say.</param>
        public static byte[] Build(CopilotInvestmentSummary summary, bool includeIndividualData, string buildLabel,
            PortalLanguage language = PortalLanguage.English)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            var words = new Words(language);
            using (var workbook = new XlsxWriter())
            {
                WriteExecutive(workbook, summary, includeIndividualData, buildLabel, words);
                WriteValue(workbook, summary, words);
                WritePath(workbook, summary, words);
                WriteCohorts(workbook, summary, words);
                WriteOrganisation(workbook, summary, words);
                WriteCredits(workbook, summary, words);
                WriteMethod(workbook, summary, words);
                if (includeIndividualData) WritePeople(workbook, summary, words);
                return workbook.ToArray();
            }
        }

        /// <summary>Indents a part of the row above it; layout, not wording.</summary>
        private const string Indent = "   ";

        /// <summary>Symbols that read the same in every language the portal ships.</summary>
        private const string PercentUnit = "%";
        private const string MultipleUnit = "x";

        private static string Currency(CopilotInvestmentSummary summary, Words words) => summary.Investment.Currency ?? words.W("noCurrency");

        private static void WriteExecutive(XlsxWriter workbook, CopilotInvestmentSummary summary, bool includeIndividualData, string buildLabel, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.executive"));
            sheet.SetColumnWidths(46, 22, 18, 70);
            sheet.AddTitle(words.W("exec.title"));
            sheet.AddRow(words.W("exec.period"), XlsxCell.Date(summary.FromUtc), XlsxCell.Date(summary.ToUtc), words.W("unit.daysCount", ("days", words.Count(summary.WindowDays))));
            sheet.AddRow(words.W("exec.generated"), summary.GeneratedUtc, null, buildLabel);
            sheet.AddRow(words.W("exec.covers"), null, null, XlsxCell.Wrapped(Coverage(summary, words)));
            sheet.AddRow(words.W("exec.assumptions"), null, null, AssumptionBasis(summary, words));
            sheet.AddRow(words.W("exec.confidence"), words.Label(Words.TrustLevel, summary.Trust.Level), null,
                XlsxCell.Wrapped(string.Join(" ", summary.Trust.Reasons.Select(r => TrustText(r, words)))));
            sheet.AddRow(words.W("exec.personData"), includeIndividualData ? words.W("exec.personDataIncluded") : words.W("exec.personDataExcluded"));
            sheet.AddRow(words.W("exec.money"), null, null, words.W("exec.moneyNote"));
            sheet.AddBlankRow();

            sheet.AddHeaderRow(words.W("exec.col.headline"), words.W("col.value"), words.W("col.unit"), words.W("exec.col.basis"));
            var seats = summary.Investment.Seats;
            var currency = Currency(summary, words);
            var people = words.W("unit.people");
            var days = words.W("unit.days");
            var credits = words.W("unit.credits");
            sheet.AddRow(words.W("exec.holders"), XlsxCell.Number(seats.Holders), people,
                seats.Basis == CopilotInvestmentCalculator.SeatBases.LicenceHistory || seats.Basis == CopilotInvestmentCalculator.SeatBases.Mixed
                    ? words.W("exec.holders.basisHistory") : words.W("exec.holders.basisCurrent"));
            sheet.AddRow(words.W("exec.analysed"), XlsxCell.Number(seats.AnalysedHolders), people, words.W("exec.analysed.basis"));
            if (seats.OtherPeriodHolders > 0)
                sheet.AddRow(words.W("exec.otherPeriod"), XlsxCell.Number(seats.OtherPeriodHolders), people, words.W("exec.otherPeriod.basis"));
            if (seats.NotAnalysedHolders > 0)
                sheet.AddRow(words.W("exec.notAnalysed"), XlsxCell.Number(seats.NotAnalysedHolders), people, words.W("exec.notAnalysed.basis"));
            sheet.AddRow(words.W("exec.runRate"), seats.MonthlyRunRate, currency, words.W("exec.runRate.basis"));
            sheet.AddRow(words.W("exec.windowCost"), seats.WindowCost, currency, words.W("exec.windowCost.basis"));
            // A figure withheld from a reader without See PII says why in its own cell, never a blank or a zero.
            var withheld = WithheldText(summary, words);
            var costWithheld = summary.Investment.StudioCostWithheld;
            sheet.AddRow(words.W("exec.modelledCost"), Shown(summary.Investment.ModelledMonthlyCost, costWithheld, withheld), currency, words.W("exec.modelledCost.basis"));
            sheet.AddRow(Indent + words.W("exec.ofSeats"), summary.Investment.MonthlySeatCost, currency, words.W("exec.ofSeats.basis"));
            sheet.AddRow(Indent + words.W("exec.ofStudio"), Shown(summary.Investment.MonthlyStudioCost, costWithheld, withheld), currency, words.W("exec.ofStudio.basis"));
            sheet.AddRow(words.W("exec.active"), XlsxCell.Number(summary.Evidence.ActiveHolders), people, words.W("exec.active.basis"));
            sheet.AddRow(words.W("exec.activeShare"), summary.Evidence.ActiveShare.HasValue ? (object)XlsxCell.Percent(summary.Evidence.ActiveShare.Value) : null,
                PercentUnit, words.W("exec.observed"));
            sheet.AddRow(words.W("exec.medianDays"), summary.Evidence.MedianActiveDays, days, words.W("exec.observed"));
            sheet.AddRow(words.W("exec.costPerActive"), seats.CostPerActiveHolderMonthly, currency, words.W("exec.costPerActive.basis"));
            foreach (var scenario in summary.Value.Scenarios)
            {
                var name = words.Label(Words.Scenario, scenario.Scenario);
                sheet.AddRow(words.W("exec.valueScenario", ("scenario", name)), scenario.MonthlyValue, currency,
                    words.W("exec.valueScenario.basis", ("minutes", words.Num(scenario.MinutesPerActiveDay)), ("realisation", words.Pct(scenario.Realisation, 0))));
                sheet.AddRow(words.W("exec.roiScenario", ("scenario", name)), Shown(scenario.RoiMultiple, costWithheld, withheld), MultipleUnit, words.W("exec.roiScenario.basis"));
            }
            if (summary.Path.Available)
                sheet.AddRow(words.W("exec.gap"), Shown(summary.Path.MonthlyGap, costWithheld, withheld), currency, words.W("exec.gap.basis"));
            sheet.AddRow(words.W("exec.breakEvenMinutes"), Shown(summary.BreakEven.MinutesPerActiveDay, costWithheld, withheld), words.W("unit.minutes"),
                words.W("exec.breakEvenMinutes.basis"));
            sheet.AddRow(words.W("exec.margin"), Shown(summary.BreakEven.MarginOfSafety.HasValue ? (object)XlsxCell.Percent(summary.BreakEven.MarginOfSafety.Value) : null,
                costWithheld, withheld), PercentUnit, words.W("exec.margin.basis"));
            sheet.AddRow(words.W("exec.seatDays"), summary.BreakEven.ActiveDaysPerMonthPerSeat, days, words.W("exec.baseAssumptions"));
            var studio = summary.Investment.Studio;
            sheet.AddRow(words.W("exec.studioCredits"), Shown(studio.Credits, studio.CreditsWithheld, withheld), credits, words.W("exec.studioCredits.basis"));
            sheet.AddRow(words.W("exec.studioPrior"), Shown(studio.PriorCredits, studio.PriorCreditsWithheld, withheld), credits,
                studio.PriorCredits.HasValue || studio.PriorCreditsWithheld ? words.W("exec.studioPrior.basis") : words.W("studio.notCompared"));
            var balances = summary.Investment.Credits;
            sheet.AddRow(words.W("exec.balances"), Shown(balances.Credits, balances.CreditsWithheld, withheld), credits, words.W("exec.balances.basis"));
            sheet.AddRow(words.W("exec.balanceValue"), Shown(balances.BalanceValue, balances.CreditsWithheld, withheld), currency, words.W("exec.balanceValue.basis"));
            sheet.AddBlankRow();

            sheet.AddHeaderRow(words.W("exec.col.assessment"), string.Empty, words.W("col.code"), words.W("exec.col.finding"));
            foreach (var finding in summary.Findings)
                sheet.AddRow(words.Label(Words.Tone, finding.Tone), null, finding.Code, XlsxCell.Wrapped(FindingText(finding, currency, words)));
            sheet.AddBlankRow();

            sheet.AddHeaderRow(words.W("exec.col.action"), words.W("col.people"), words.W("exec.col.amount"), words.W("exec.col.meaning"), words.W("col.code"));
            foreach (var action in summary.Actions)
            {
                var meaning = ActionText(action, words);
                var effect = action.Effect != null && words.Has(Words.Effect + action.Effect) ? words.Label(Words.Effect, action.Effect) : null;
                sheet.AddRow(ActionTitle(action.Code, words), XlsxCell.Number(action.People), Shown(action.MonthlyAmount, action.MonthlyAmountWithheld, withheld),
                    XlsxCell.Wrapped(effect == null ? meaning : effect + " " + meaning), action.Code);
            }
        }

        /// <summary>What a cell withheld from a reader without See PII says instead of a figure: why, never a blank or a zero.</summary>
        private static string WithheldText(CopilotInvestmentSummary summary, Words words) =>
            words.T("copilotInvestment.trend.withheld",
                ("count", words.Count(Math.Max(summary.Organisation?.MinGroupSize ?? 0, SmallPopulations.MinPeopleWithoutSeePii))));

        private static object Shown(object value, bool withheld, string withheldText) => withheld ? withheldText : value;

        /// <summary>Who the figures cover: everyone, or the administrator's filter and the reader's own, in the reader's language.</summary>
        private static string Coverage(CopilotInvestmentSummary summary, Words words)
        {
            if (!summary.Filtered) return words.W("exec.everyone");
            var lines = new List<string>();
            var global = FilterWording.Describe(summary.GlobalFilter, words.Language);
            if (!string.IsNullOrEmpty(global)) lines.Add(words.W("exec.filterAdmin", ("filter", global)));
            var reader = FilterWording.Describe(summary.UserFilter, words.Language);
            if (!string.IsNullOrEmpty(reader)) lines.Add(words.W("exec.filterReader", ("filter", reader)));
            return lines.Count > 0 ? string.Join("\n", lines) : words.W("exec.filteredUnknown");
        }

        private static string AssumptionBasis(CopilotInvestmentSummary summary, Words words)
        {
            var view = summary.Assumptions;
            switch (view.Status)
            {
                case CopilotInvestmentAssumptionStatus.Unavailable: return words.W("basis.unavailable");
                case CopilotInvestmentAssumptionStatus.Missing: return words.W("basis.missing");
                case CopilotInvestmentAssumptionStatus.Partial:
                    var partial = words.W("basis.partial", ("fields", FieldList(view.MissingForValue, words)));
                    return view.ScenarioApplied ? partial + " " + words.W("basis.whatIf") : partial;
                default: return view.ScenarioApplied ? words.W("basis.tenantWhatIf") : words.W("basis.tenant");
            }
        }

        /// <summary>Assumption names as the page shows them, in a list.</summary>
        private static string FieldList(IEnumerable<string> fields, Words words) =>
            string.Join(", ", (fields ?? Enumerable.Empty<string>()).Select(f => FieldLabel(f, words)));
    }
}

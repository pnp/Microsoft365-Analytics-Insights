using Common.Entities.Xlsx;
using System;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    public static partial class CopilotInvestmentWorkbook
    {
        private static void WriteMethod(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.method"));
            sheet.SetColumnWidths(44, 20, 16, 90);
            var view = summary.Assumptions;
            sheet.AddHeaderRow(words.W("method.col.assumption"), words.W("col.value"), words.W("method.col.source"), words.W("method.col.meaning"));
            var values = view.Values ?? new CopilotInvestmentAssumptions();
            void Row(string field, object value, string meaningKey)
            {
                view.Provenance.TryGetValue(field, out var provenance);
                sheet.AddRow(FieldLabel(field, words), value, words.Label(Words.Provenance, provenance ?? CopilotInvestmentProvenance.Missing), words.W(meaningKey));
            }
            Row(CopilotInvestmentFields.Currency, values.Currency, "method.meaning.currency");
            Row(CopilotInvestmentFields.SeatPricePerMonth, values.SeatPricePerMonth, "method.meaning.seatPrice");
            Row(CopilotInvestmentFields.CreditPrice, values.CreditPrice, "method.meaning.creditPrice");
            Row(CopilotInvestmentFields.StudioCreditPrice, values.StudioCreditPrice, "method.meaning.studioCreditPrice");
            Row(CopilotInvestmentFields.HourlyCost, values.HourlyCost, "method.meaning.hourlyCost");
            Row(CopilotInvestmentFields.MinutesConservative, values.MinutesPerActiveDay?.Conservative, "method.meaning.minutesConservative");
            Row(CopilotInvestmentFields.MinutesBase, values.MinutesPerActiveDay?.Base, "method.meaning.minutesBase");
            Row(CopilotInvestmentFields.MinutesUpside, values.MinutesPerActiveDay?.Upside, "method.meaning.minutesUpside");
            Row(CopilotInvestmentFields.RealisationConservative, values.Realisation?.Conservative, "method.meaning.realisationConservative");
            Row(CopilotInvestmentFields.RealisationBase, values.Realisation?.Base, "method.meaning.realisationBase");
            Row(CopilotInvestmentFields.RealisationUpside, values.Realisation?.Upside, "method.meaning.realisationUpside");
            sheet.AddRow(words.W("method.note"), view.Note);
            sheet.AddRow(words.W("method.version"), XlsxCell.Number(view.Version), null, view.UpdatedUtc.HasValue
                ? words.W("method.lastSaved", ("when", view.UpdatedUtc.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                : words.W("method.neverSaved"));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("method.col.formula"), string.Empty, string.Empty, words.W("method.col.definition"));
            void Formula(string name, string definitionKey) => sheet.AddRow(name, null, null, XlsxCell.Wrapped(words.W(definitionKey)));
            Formula(words.W("method.seatCost"), "method.seatCost.definition");
            Formula(words.W("method.value"), "method.value.definition");
            Formula(words.W("method.roi"), "method.roi.definition");
            Formula(words.W("exec.breakEvenMinutes"), "method.breakEven.definition");
            Formula(words.W("exec.seatDays"), "method.seatDays.definition");
            Formula(words.W("method.extraDays"), "method.extraDays.definition");
            Formula(words.W("method.balances"), "method.balances.definition");
            Formula(words.W("path.col.concentration"), "method.concentration.definition");
            Formula(words.W("method.notSaving"), "method.notSaving.definition");
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("exec.confidence"), words.W("method.col.level"), string.Empty, string.Empty);
            foreach (var area in summary.Trust.Areas) sheet.AddRow(words.Label(Words.TrustArea, area.Area), words.Label(Words.TrustLevel, area.Level));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("method.col.area"), words.W("method.col.impact"), words.W("col.code"), words.W("method.col.detail"));
            foreach (var reason in summary.Trust.Reasons)
                sheet.AddRow(words.Label(Words.TrustArea, reason.Area), words.Label(Words.Impact, reason.Impact), reason.Code, XlsxCell.Wrapped(TrustText(reason, words)));
        }

        private static void WritePeople(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.people"));
            var headers = new[]
            {
                words.W("people.col.upn"), words.W("people.col.department"), words.W("people.col.jobTitle"), words.W("people.col.country"),
                words.W("people.col.company"), words.W("people.col.manager"), words.W("people.col.seat"), words.W("people.col.seatLicences"),
                words.W("people.col.seatDays"), words.W("people.col.seatBasis"), words.T("copilotInvestment.value.col.band"), words.W("people.col.signalSource"),
                words.W("col.activeDays"), words.W("people.col.activeDaysMonth"), words.W("people.col.habitShare"), words.W("people.col.apps"),
                words.W("people.col.agents"), words.W("people.col.cowork"), words.W("people.col.interactions"), words.W("people.col.lastActivity"),
                words.T("copilotInvestment.signal.tooNewToJudge"), words.W("people.col.reclaim"), words.W("people.col.recommended"),
                words.W("people.col.balance"), words.W("people.col.balanceValue"), words.W("people.col.balanceDate"), words.W("people.col.studioCredits"),
                words.W("people.col.studioCost"), words.W("people.col.seatCost"), words.W("people.col.cost"), words.W("people.col.hoursBase"),
                words.W("people.col.valueConservative"), words.W("people.col.valueBase"), words.W("people.col.valueUpside"), words.W("people.col.valueToCost"),
                words.W("people.col.breakEvenDays"), words.W("people.col.gapDays"), words.W("people.col.signal"), words.W("people.col.flags"),
            };
            sheet.SetColumnWidths(headers.Select(h => (double)Math.Max(12, Math.Min(30, h.Length + 4))).ToArray());
            sheet.AddHeaderRow(headers);
            foreach (var p in summary.People.OrderBy(p => p.UserPrincipalName, StringComparer.OrdinalIgnoreCase))
            {
                sheet.AddRow(p.UserPrincipalName, p.Department, p.JobTitle, p.Country, p.Company, p.Manager, p.Licensed, p.SeatLicences,
                    p.SeatDays, words.Label(Words.SeatBasis, p.SeatBasis), words.Label(Words.Band, p.Band), words.Label(Words.SignalSource, p.SignalSource),
                    XlsxCell.Number(p.ActiveDays), p.ActiveDaysPerMonth, p.HabitShare.HasValue ? (object)XlsxCell.Percent(p.HabitShare.Value) : null,
                    XlsxCell.Number(p.AppsUsed), XlsxCell.Number(p.AgentsUsed), XlsxCell.Number(p.CoworkInteractions), XlsxCell.Number(p.Interactions),
                    p.LastActivityUtc, p.TooNewToJudge, words.Label(Words.Reclaim, p.ReclaimEligibility), p.RecommendedForLicence, p.Credits, p.CreditsValue,
                    p.CreditObservationDate, p.StudioCredits, p.StudioCost, p.SeatCost, p.Cost, p.HoursBase, p.ValueConservative, p.ValueBase, p.ValueUpside,
                    p.ValueToCostBase, p.BreakEvenDaysPerMonth, p.BreakEvenGapDaysPerMonth, words.Label(Words.Signal, p.Signal),
                    string.Join(", ", p.Flags.Select(f => words.Label(Words.Flag, f))));
            }
            sheet.FreezeTopRows(1);
            if (sheet.CurrentRow > 1) sheet.AddAutoFilter(1, sheet.CurrentRow, 1, headers.Length);
        }
    }
}

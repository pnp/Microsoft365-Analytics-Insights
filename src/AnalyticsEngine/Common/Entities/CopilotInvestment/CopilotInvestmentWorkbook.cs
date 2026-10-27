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
    /// <para>The workbook is written in English, like the product's other workbooks.</para>
    /// </remarks>
    public static class CopilotInvestmentWorkbook
    {
        public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        public static string FileName(CopilotInvestmentSummary summary) =>
            string.Format(CultureInfo.InvariantCulture, "copilot-investment-{0:yyyy-MM-dd}-{1}d.xlsx", summary.GeneratedUtc, summary.WindowDays);

        public static byte[] Build(CopilotInvestmentSummary summary, bool includeIndividualData, string buildLabel)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            using (var workbook = new XlsxWriter())
            {
                WriteExecutive(workbook, summary, includeIndividualData, buildLabel);
                WriteValue(workbook, summary);
                WriteCohorts(workbook, summary);
                WriteOrganisation(workbook, summary);
                WriteCredits(workbook, summary);
                WriteMethod(workbook, summary);
                if (includeIndividualData) WritePeople(workbook, summary);
                return workbook.ToArray();
            }
        }

        private static string Currency(CopilotInvestmentSummary summary) => summary.Investment.Currency ?? "(no currency set)";

        private static void WriteExecutive(XlsxWriter workbook, CopilotInvestmentSummary summary, bool includeIndividualData, string buildLabel)
        {
            var sheet = workbook.AddSheet("Executive summary");
            sheet.SetColumnWidths(46, 22, 18, 70);
            sheet.AddTitle("Microsoft 365 Copilot investment and value");
            sheet.AddRow("Period (UTC)", XlsxCell.Date(summary.FromUtc), XlsxCell.Date(summary.ToUtc), summary.WindowDays + " days");
            sheet.AddRow("Generated (UTC)", summary.GeneratedUtc, null, buildLabel);
            sheet.AddRow("Who this covers", null, null, summary.Filtered
                ? "Filtered: " + string.Join("; ", new[] { summary.GlobalFilterDescription, summary.UserFilterDescription }.Where(d => !string.IsNullOrWhiteSpace(d)))
                : "Everyone in the tenant");
            sheet.AddRow("Assumptions", null, null, AssumptionBasis(summary));
            sheet.AddRow("Confidence", summary.Trust.Level, null, string.Join(" ", summary.Trust.Reasons.Select(r => TrustText(r))));
            sheet.AddRow("Per-person data", includeIndividualData ? "Included" : "Not included (needs the See PII permission)");
            sheet.AddRow("Money", null, null, "Every amount is modelled from the prices and hourly cost in the Assumptions sheet. None is an invoice or a measured saving.");
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Headline", "Value", "Unit", "Basis");
            var seats = summary.Investment.Seats;
            var currency = Currency(summary);
            sheet.AddRow("Copilot seat holders", XlsxCell.Number(seats.Holders), "people", "Current Copilot licence assignments" + (seats.Basis == "licenceHistory" ? " and licence history" : string.Empty));
            sheet.AddRow("Seat run-rate per month", seats.MonthlyRunRate, currency, "Seat holders x seat price (modelled)");
            sheet.AddRow("Seat cost in the period", seats.WindowCost, currency, "Seat price x seat-days / average month (modelled)");
            sheet.AddRow("Seat holders with Copilot activity", XlsxCell.Number(summary.Evidence.ActiveHolders), "people", "At least one active day in the period");
            sheet.AddRow("Active share of analysed seats", summary.Evidence.ActiveShare.HasValue ? (object)XlsxCell.Percent(summary.Evidence.ActiveShare.Value) : null, "%", "Observed");
            sheet.AddRow("Median active days (active holders)", summary.Evidence.MedianActiveDays, "days", "Observed");
            sheet.AddRow("Cost per active seat holder per month", seats.CostPerActiveHolderMonthly, currency, "Modelled");
            foreach (var scenario in summary.Value.Scenarios)
            {
                sheet.AddRow("Modelled value per month (" + scenario.Scenario + ")", scenario.MonthlyValue, currency,
                    string.Format(CultureInfo.InvariantCulture, "{0:0.##} min per active day x {1:0%} realised x hourly cost", scenario.MinutesPerActiveDay, scenario.Realisation));
                sheet.AddRow("Return multiple (" + scenario.Scenario + ")", scenario.RoiMultiple, "x", "Modelled value / modelled cost of the same seat holders");
            }
            sheet.AddRow("Break-even minutes per active day", summary.BreakEven.MinutesPerActiveDay, "minutes", "At base realisation and hourly cost");
            sheet.AddRow("Margin of safety", summary.BreakEven.MarginOfSafety.HasValue ? (object)XlsxCell.Percent(summary.BreakEven.MarginOfSafety.Value) : null, "%", "1 - break-even minutes / base minutes");
            sheet.AddRow("Active days a month one seat needs", summary.BreakEven.ActiveDaysPerMonthPerSeat, "days", "Base assumptions");
            sheet.AddRow("Copilot Studio billed credits in the period", summary.Investment.Studio.Credits, "credits", "Daily ledger, attributed to people");
            sheet.AddRow("Copilot Studio credits, previous period", summary.Investment.Studio.PriorCredits, "credits", "Same ledger, equal-length period before");
            sheet.AddRow("Latest preview credit balances", summary.Investment.Credits.Credits, "credits", "Balance in the source's own period (unverified); not spend");
            sheet.AddRow("Balance valued at the credit price", summary.Investment.Credits.BalanceValue, currency, "A valuation of a balance, not a period cost");
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Finding", "Tone", "", "Detail");
            foreach (var finding in summary.Findings) sheet.AddRow(finding.Code, finding.Tone, null, XlsxCell.Wrapped(FindingText(finding, currency)));
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Recommended action", "People", "Amount per month", "What it means");
            foreach (var action in summary.Actions)
                sheet.AddRow(action.Code, XlsxCell.Number(action.People), action.MonthlyAmount, XlsxCell.Wrapped(ActionText(action, currency)));
        }

        private static void WriteValue(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Value and break-even");
            sheet.SetColumnWidths(18, 16, 14, 16, 16, 18, 18, 14, 18, 22);
            sheet.AddTitle("Modelled value, break-even and sensitivity");
            if (!summary.Value.Available)
            {
                sheet.AddRow("Modelled value is unavailable until these assumptions are set: " + string.Join(", ", summary.Value.Missing));
                return;
            }
            sheet.AddHeaderRow("Scenario", "Minutes per active day", "Realisation", "Assisted hours", "Realised hours", "Value in period", "Value per month", "Return multiple", "Net in period", "Holders at or above break-even");
            foreach (var s in summary.Value.Scenarios)
                sheet.AddRow(s.Scenario, s.MinutesPerActiveDay, XlsxCell.Percent(s.Realisation), s.AssistedHours, s.RealisedHours, s.Value, s.MonthlyValue, s.RoiMultiple, s.Net, XlsxCell.Number(s.HoldersAtOrAboveBreakEven));
            sheet.AddBlankRow();
            var b = summary.BreakEven;
            sheet.AddHeaderRow("Break-even", "Value");
            sheet.AddRow("Minutes per active day the analysed seats need", b.MinutesPerActiveDay);
            sheet.AddRow("Margin of safety against the base minutes", b.MarginOfSafety.HasValue ? (object)XlsxCell.Percent(b.MarginOfSafety.Value) : null);
            sheet.AddRow("Active days a month one seat needs", b.ActiveDaysPerMonthPerSeat);
            sheet.AddRow("Realised minutes per working day one seat needs", b.MinutesPerWeekdayPerSeat);
            sheet.AddRow("Seat holders at or above break-even", XlsxCell.Number(b.HoldersAtOrAbove));
            sheet.AddRow("Seat holders below break-even", XlsxCell.Number(b.HoldersBelow));
            sheet.AddRow("Seat holders with no activity", XlsxCell.Number(b.HoldersNoEvidence));
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Minutes scenario", "Realisation scenario", "Value in period", "Return multiple");
            foreach (var cell in summary.Sensitivity.Grid) sheet.AddRow(cell.MinutesScenario, cell.RealisationScenario, cell.Value, cell.RoiMultiple);
            sheet.AddBlankRow();
            sheet.AddRow("Base return if the hourly cost were 20% lower", summary.Sensitivity.RoiBaseHourlyMinus20);
            sheet.AddRow("Base return if the hourly cost were 20% higher", summary.Sensitivity.RoiBaseHourlyPlus20);
            sheet.AddRow("Base return after reassigning reclaimable idle seats", summary.Sensitivity.RoiBaseAfterReclaim);
            sheet.AddRow("Base return IF each latest credit balance were one month of use (unverified)", summary.Sensitivity.RoiBaseIfBalancesMonthly);
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Active days per month", "Seat holders", "Contains break-even");
            foreach (var bucket in summary.Evidence.Distribution)
                sheet.AddRow(bucket.Key, XlsxCell.Number(bucket.Holders), bucket.BreakEvenBucket);
        }

        private static void WriteCohorts(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Adoption cohorts");
            sheet.SetColumnWidths(16, 12, 14, 14, 14, 16, 16, 16, 14, 14, 14);
            sheet.AddHeaderRow("Adoption band", "Seat holders", "Share of seats", "Active days", "Share of active days", "Median active days", "Modelled cost", "Modelled value (base)", "Return (base)", "Credits (balance)", "Studio credits");
            foreach (var c in summary.Cohorts)
                sheet.AddRow(c.Band, XlsxCell.Number(c.Holders), c.ShareOfHolders.HasValue ? (object)XlsxCell.Percent(c.ShareOfHolders.Value) : null,
                    XlsxCell.Number(c.ActiveDays), c.ShareOfActiveDays.HasValue ? (object)XlsxCell.Percent(c.ShareOfActiveDays.Value) : null,
                    c.MedianActiveDays, c.Cost, c.ValueBase, c.RoiBase, c.Credits, c.StudioCredits);
            sheet.FreezeTopRows(1);
        }

        private static void WriteOrganisation(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Organisation");
            var organisation = summary.Organisation;
            sheet.SetColumnWidths(34, 12, 12, 12, 12, 16, 14, 16, 12, 14, 14, 14, 16);
            sheet.AddRow("Grouped by", organisation.DimensionName ?? organisation.Dimension, null, "Groups smaller than " + organisation.MinGroupSize + " people are combined.");
            if (organisation.Hidden)
            {
                sheet.AddRow("This grouping names people, so it needs the See PII permission.");
                return;
            }
            sheet.AddHeaderRow("Group", "Seat holders", "Active", "Idle", "Active share", "Active days per holder per month", "Modelled cost", "Modelled value (base)", "Return (base)", "Credits (balance)", "Credit consumers", "Studio credits", "Unlicensed Copilot users");
            var first = sheet.CurrentRow;
            foreach (var g in organisation.Groups)
                sheet.AddRow(g.Label ?? (g.EmptyKey == CopilotInvestmentCalculator.GroupKeys.NotSet ? "(not set)" : "(smaller groups combined)"),
                    XlsxCell.Number(g.Holders), XlsxCell.Number(g.ActiveHolders), XlsxCell.Number(g.IdleHolders),
                    g.ActiveShare.HasValue ? (object)XlsxCell.Percent(g.ActiveShare.Value) : null, g.ActiveDaysPerHolderMonthly,
                    g.Cost, g.ValueBase, g.RoiBase, g.Credits, XlsxCell.Number(g.CreditConsumers), g.StudioCredits, XlsxCell.Number(g.UnlicensedActiveUsers));
            sheet.FreezeTopRows(first);
            if (sheet.CurrentRow > first) sheet.AddAutoFilter(first, sheet.CurrentRow, 1, 13);
        }

        private static void WriteCredits(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Credits and sources");
            sheet.SetColumnWidths(40, 18, 16, 16, 16, 16, 16, 40);
            var source = summary.Sources.Credits;
            sheet.AddTitle("Copilot credit balances (preview) and Copilot Studio ledger");
            sheet.AddRow("Credit balances are point-in-time balances in the source's own period, which the source does not identify. They are never added across days and never treated as daily or monthly spend.");
            sheet.AddRow("Snapshot date", source.SnapshotDate, null, "Latest observation per person within " + CopilotInvestmentSql.SnapshotBandDays + " days of this date");
            sheet.AddRow("First observation recorded", source.FirstObservationDate);
            sheet.AddRow("Period status", source.PeriodStatus ?? "(no data)");
            sheet.AddRow("People with a balance in this view", XlsxCell.Number(source.ObservedUsers));
            sheet.AddRow("Partial observations", XlsxCell.Number(source.PartialUsers));
            sheet.AddRow("Directory objects not matched to a user (tenant)", source.UnresolvedObjects);
            sheet.AddRow("Credits on unmatched objects (tenant)", source.UnresolvedCredits);
            sheet.AddRow("Sweeps started in the period", XlsxCell.Number(source.SweepsInWindow));
            sheet.AddRow("Sweeps completed in the period", XlsxCell.Number(source.CompletedSweepsInWindow));
            sheet.AddRow("Latest sweep: directory users", source.DirectoryUsers);
            sheet.AddRow("Latest sweep: observed", source.CollectorObserved);
            sheet.AddRow("Latest sweep: failed", source.CollectorFailed);
            sheet.AddRow("Latest sweep: denied or missing", source.CollectorDenied);
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Service", "Unit", "People consuming", "Quantity", "Unknown amounts", "Limit exhausted", "Not entitled", "Policies");
            foreach (var s in summary.Services)
                sheet.AddRow(s.Category, s.Unit ?? "(unknown unit)", XlsxCell.Number(s.Consumers), s.Quantity, XlsxCell.Number(s.UnknownAmountUsers),
                    XlsxCell.Number(s.LimitExhaustedUsers), XlsxCell.Number(s.NotEntitledUsers), string.Join(", ", s.Policies));
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Spending policy (tenant-wide)", "Type", "Status", "Billing methods", "Period limit", "Per-user limit", "Assigned groups", "Services");
            foreach (var p in summary.Policies)
                sheet.AddRow(p.DisplayName ?? p.Id, p.Type, p.Status, p.BillingMethods, QuantityText(p.PeriodLimit), QuantityText(p.PerUserLimit),
                    p.AssignedGroups, string.Join(", ", p.Services.Select(s => s.ServiceId + " (" + s.State + ")")));
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Tenant balance (tenant-wide)", "Purchased", "Consumed", "Remaining", "Unit", "Last modified");
            foreach (var t in summary.TenantBalances)
                sheet.AddRow(t.BillingMethod, t.Purchased, t.Consumed, t.Remaining, t.Unit, t.LastModified);
            sheet.AddBlankRow();
            var studio = summary.Investment.Studio;
            sheet.AddHeaderRow("Copilot Studio ledger", "Value");
            sheet.AddRow("People with billed credits", XlsxCell.Number(studio.Users));
            sheet.AddRow("Billed credits in the period", studio.Credits);
            sheet.AddRow("Billed credits, previous period", studio.PriorCredits);
            sheet.AddRow("Of which seat holders", studio.HolderCredits);
            sheet.AddRow("Valued at the Studio credit price", studio.WindowValue);
        }

        private static string QuantityText(CopilotInvestmentQuantity quantity)
        {
            if (quantity == null) return null;
            if (quantity.Kind == CopilotInvestmentQuantityKinds.Unlimited) return "unlimited";
            return quantity.Value.HasValue
                ? quantity.Value.Value.ToString("0.######", CultureInfo.InvariantCulture) + " " + (quantity.Unit ?? string.Empty)
                : quantity.Kind;
        }

        private static void WriteMethod(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Assumptions and method");
            sheet.SetColumnWidths(44, 20, 16, 90);
            var view = summary.Assumptions;
            sheet.AddHeaderRow("Assumption", "Value", "Source", "Meaning");
            var values = view.Values ?? new CopilotInvestmentAssumptions();
            void Row(string field, object value, string meaning)
            {
                view.Provenance.TryGetValue(field, out var provenance);
                sheet.AddRow(field, value, provenance ?? CopilotInvestmentProvenance.Missing, meaning);
            }
            Row(CopilotInvestmentFields.Currency, values.Currency, "Currency every amount is in.");
            Row(CopilotInvestmentFields.SeatPricePerMonth, values.SeatPricePerMonth, "What one Copilot seat costs per person per month.");
            Row(CopilotInvestmentFields.CreditPrice, values.CreditPrice, "What one Copilot credit costs; used to value preview balances only.");
            Row(CopilotInvestmentFields.StudioCreditPrice, values.StudioCreditPrice, "What one billed Copilot Studio credit costs.");
            Row(CopilotInvestmentFields.HourlyCost, values.HourlyCost, "Fully loaded cost of one hour of an employee's time.");
            Row(CopilotInvestmentFields.MinutesConservative, values.MinutesPerActiveDay?.Conservative, "Minutes Copilot assists on a day it is used: conservative.");
            Row(CopilotInvestmentFields.MinutesBase, values.MinutesPerActiveDay?.Base, "Minutes Copilot assists on a day it is used: base.");
            Row(CopilotInvestmentFields.MinutesUpside, values.MinutesPerActiveDay?.Upside, "Minutes Copilot assists on a day it is used: upside.");
            Row(CopilotInvestmentFields.RealisationConservative, values.Realisation?.Conservative, "Share of assisted time that becomes business value: conservative.");
            Row(CopilotInvestmentFields.RealisationBase, values.Realisation?.Base, "Share of assisted time that becomes business value: base.");
            Row(CopilotInvestmentFields.RealisationUpside, values.Realisation?.Upside, "Share of assisted time that becomes business value: upside.");
            sheet.AddRow("Administrator's note", view.Note);
            sheet.AddRow("Assumptions version", XlsxCell.Number(view.Version), null, view.UpdatedUtc.HasValue ? "Last saved " + view.UpdatedUtc.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "Never saved");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Formula", "", "", "Definition");
            sheet.AddRow("Seat cost", null, null, "Seat price per month x seat-days in the period / 30.44. Seat-days come from licence history when imported, otherwise the whole period for a current assignment.");
            sheet.AddRow("Modelled value", null, null, "Active days x minutes per active day / 60 x realisation x hourly cost. Counted per active day: more prompts on the same day add nothing.");
            sheet.AddRow("Return multiple", null, null, "Modelled value / modelled cost, for the same analysed seat holders. Cost adds their own billed Copilot Studio credits when a Studio price is set.");
            sheet.AddRow("Break-even minutes per active day", null, null, "Modelled cost / (total active days x base realisation x hourly cost / 60).");
            sheet.AddRow("Active days a month one seat needs", null, null, "Seat price / (base minutes / 60 x base realisation x hourly cost).");
            sheet.AddRow("Credit balances", null, null, "Latest observation per person; never summed across days, never treated as spend, and excluded from the return.");
            sheet.AddRow("Concentration", null, null, "Share of the total held by the top 20% of seat holders (active days) or top 10% of consumers (credits); Gini coefficient over the same population.");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Confidence reason", "Impact", "", "Detail");
            foreach (var reason in summary.Trust.Reasons) sheet.AddRow(reason.Code, reason.Impact, null, XlsxCell.Wrapped(TrustText(reason)));
        }

        private static void WritePeople(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("People");
            var headers = new[]
            {
                "User principal name", "Department", "Job title", "Country", "Company", "Manager", "Copilot seat", "Seat licences",
                "Seat days", "Seat basis", "Adoption band", "Signal source", "Active days", "Active days per month", "Share of working days",
                "Apps used", "Agents used", "Cowork interactions", "Interactions", "Last activity (UTC)", "Too new to judge",
                "Reclaim eligibility", "Recommended for a seat", "Credit balance", "Balance value", "Balance date", "Studio credits",
                "Studio cost", "Seat cost", "Modelled cost", "Realised hours (base)", "Value (conservative)", "Value (base)",
                "Value (upside)", "Value to cost (base)", "Extra active days a month to break even", "Investment signal", "Flags",
            };
            sheet.SetColumnWidths(headers.Select(h => (double)Math.Max(12, Math.Min(30, h.Length + 4))).ToArray());
            sheet.AddHeaderRow(headers);
            foreach (var p in summary.People.OrderBy(p => p.UserPrincipalName, StringComparer.OrdinalIgnoreCase))
            {
                sheet.AddRow(p.UserPrincipalName, p.Department, p.JobTitle, p.Country, p.Company, p.Manager, p.Licensed, p.SeatLicences,
                    p.SeatDays, p.SeatBasis, p.Band, p.SignalSource, XlsxCell.Number(p.ActiveDays), p.ActiveDaysPerMonth,
                    p.HabitShare.HasValue ? (object)XlsxCell.Percent(p.HabitShare.Value) : null, XlsxCell.Number(p.AppsUsed), XlsxCell.Number(p.AgentsUsed),
                    XlsxCell.Number(p.CoworkInteractions), XlsxCell.Number(p.Interactions), p.LastActivityUtc, p.TooNewToJudge,
                    p.ReclaimEligibility, p.RecommendedForLicence, p.Credits, p.CreditsValue, p.CreditObservationDate, p.StudioCredits,
                    p.StudioCost, p.SeatCost, p.Cost, p.HoursBase, p.ValueConservative, p.ValueBase, p.ValueUpside, p.ValueToCostBase,
                    p.BreakEvenGapDaysPerMonth, p.Signal, string.Join(", ", p.Flags));
            }
            sheet.FreezeTopRows(1);
            if (sheet.CurrentRow > 1) sheet.AddAutoFilter(1, sheet.CurrentRow, 1, headers.Length);
        }

        private static string AssumptionBasis(CopilotInvestmentSummary summary)
        {
            var view = summary.Assumptions;
            switch (view.Status)
            {
                case CopilotInvestmentAssumptionStatus.Unavailable: return "The saved assumptions could not be read, so no money is modelled.";
                case CopilotInvestmentAssumptionStatus.Missing: return "No assumptions are set, so no money is modelled.";
                case CopilotInvestmentAssumptionStatus.Partial: return "Some assumptions are missing: " + string.Join(", ", view.MissingForValue) + (view.ScenarioApplied ? ". Includes the reader's own what-if figures." : ".");
                default: return view.ScenarioApplied ? "Tenant assumptions with the reader's own what-if figures (not saved)." : "Tenant assumptions set by an administrator.";
            }
        }

        private static string Num(double? value, string format = "0.##") =>
            value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";

        private static string Pct(double? value) => value.HasValue ? (value.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "-";

        internal static string FindingText(CopilotInvestmentFinding finding, string currency)
        {
            double? V(string key) => finding.Values.TryGetValue(key, out var v) ? v : null;
            switch (finding.Code)
            {
                case CopilotInvestmentFindingCodes.RunRate:
                    return $"Seats cost {Num(V("monthly"), "#,0.##")} {currency} a month (modelled) for {Num(V("holders"), "#,0")} seat holders; {Num(V("costPerActive"), "#,0.##")} {currency} per seat holder who used Copilot.";
                case CopilotInvestmentFindingCodes.ValueRange:
                    return $"Modelled value is {Num(V("low"), "#,0")} to {Num(V("high"), "#,0")} {currency} a month (base {Num(V("base"), "#,0")}) against {Num(V("cost"), "#,0")} {currency} of modelled cost: a return of {Num(V("roiLow"))}x to {Num(V("roiHigh"))}x (base {Num(V("roiBase"))}x).";
                case CopilotInvestmentFindingCodes.BreakEven:
                    return $"The seats break even at {Num(V("minutes"))} minutes per active day against {Num(V("assumed"))} assumed (margin of safety {Pct(V("margin"))}); one seat needs about {Num(V("daysPerMonth"))} active days a month. {Pct(V("shareAtOrAbove"))} of analysed seats already reach it.";
                case CopilotInvestmentFindingCodes.IdleSeats:
                    return $"{Num(V("idle"), "#,0")} seat holders ({Pct(V("share"))}) used Copilot on no day in the period; {Num(V("reclaimable"), "#,0")} are reclaim candidates worth {Num(V("monthly"), "#,0.##")} {currency} a month.";
                case CopilotInvestmentFindingCodes.EvidenceConcentration:
                    return $"The most active {Pct(V("topFraction"))} of seat holders account for {Pct(V("topShare"))} of all Copilot active days (Gini {Num(V("gini"))}).";
                case CopilotInvestmentFindingCodes.CreditConcentration:
                    return $"The top {Pct(V("topFraction"))} of {Num(V("consumers"), "#,0")} credit consumers hold {Pct(V("topShare"))} of the latest balances.";
                case CopilotInvestmentFindingCodes.HighSpendThinEvidence:
                    return $"{Num(V("people"), "#,0")} people are among the largest credit balances but show thin Copilot activity ({Num(V("credits"), "#,0.##")} credits). Review what drives that consumption.";
                case CopilotInvestmentFindingCodes.ExpandCandidates:
                    return $"{Num(V("people"), "#,0")} people without a seat already show enough Copilot use to justify one; licensing them would cost {Num(V("monthly"), "#,0.##")} {currency} a month (modelled).";
                case CopilotInvestmentFindingCodes.LimitBlocked:
                    return $"{Num(V("people"), "#,0")} people have exhausted a per-user credit limit; {Num(V("evidenced"), "#,0")} of them show strong Copilot activity.";
                case CopilotInvestmentFindingCodes.AssumptionsMissing:
                    return $"{Num(V("missing"), "0")} assumptions are missing, so money, return and break-even are not modelled.";
                case CopilotInvestmentFindingCodes.BalancesUnverified:
                    return $"Latest balances hold {Num(V("credits"), "#,0.##")} credits across {Num(V("consumers"), "#,0")} consumers. The source does not identify their period, so they are not counted as spend.";
                case CopilotInvestmentFindingCodes.StudioTrend:
                    return $"Copilot Studio billed {Num(V("credits"), "#,0.##")} credits in the period against {Num(V("prior"), "#,0.##")} in the equal period before ({Pct(V("change"))}).";
                case CopilotInvestmentFindingCodes.NoSeats:
                    return "Nobody in this view holds a Copilot seat.";
                default:
                    return finding.Code;
            }
        }

        internal static string ActionText(CopilotInvestmentAction action, string currency)
        {
            switch (action.Code)
            {
                case CopilotInvestmentActionCodes.ReclaimIdle: return $"Review and reassign seats with no Copilot activity that the adoption report rates certain or probable to reclaim. Base return afterwards: {Num(action.RoiBaseAfter)}x.";
                case CopilotInvestmentActionCodes.EnableBelowBreakEven: return $"Coach seat holders who use Copilot but below break-even; the median holder needs about {Num(action.Metric)} more active days a month.";
                case CopilotInvestmentActionCodes.EnableLightEvidence: return "Coach seat holders with only light Copilot activity.";
                case CopilotInvestmentActionCodes.ExpandEvidenced: return $"Offer seats to people without one whose Copilot use already exceeds the {Num(action.Metric)} active days a month a seat needs.";
                case CopilotInvestmentActionCodes.ExpandRecommended: return "Offer seats to the people the adoption report recommends.";
                case CopilotInvestmentActionCodes.ReviewHighSpendThinEvidence: return $"Review the largest credit balances that come with thin Copilot activity ({Num(action.Credits, "#,0.##")} credits)." + (action.AssumesMonthlyBalances ? " The amount assumes each balance is one month of use, which the source does not confirm." : string.Empty);
                case CopilotInvestmentActionCodes.RaiseLimitEvidenced: return "Consider raising per-user limits for people with strong Copilot activity who have reached or nearly reached them.";
                case CopilotInvestmentActionCodes.LicenceMeteredHeavy: return "People without a seat whose billed Copilot Studio credits already cost at least a seat each month.";
                case CopilotInvestmentActionCodes.ImproveCoverage: return "Resolve credit observations that are partial, stale or not matched to an imported user.";
                case CopilotInvestmentActionCodes.SetAssumptions: return "Ask a portal administrator to set the seat price, hourly cost and productivity assumptions.";
                default: return action.Code;
            }
        }

        internal static string TrustText(CopilotInvestmentTrustReason reason)
        {
            double? V(string key) => reason.Values.TryGetValue(key, out var v) ? v : null;
            switch (reason.Code)
            {
                case CopilotInvestmentTrustCodes.AdoptionUnavailable: return "The Copilot adoption evidence is unavailable.";
                case CopilotInvestmentTrustCodes.AuditMissing: return "The Copilot audit import is unavailable, so evidence is limited to Microsoft's usage report or missing.";
                case CopilotInvestmentTrustCodes.UsageReportSignal: return $"{Num(V("holders"), "#,0")} seat holders are scored from Microsoft's usage report, whose period is Microsoft's.";
                case CopilotInvestmentTrustCodes.FiguresIncomplete: return "Some adoption queries failed, so figures are incomplete.";
                case CopilotInvestmentTrustCodes.HoldersNotAnalysed: return $"{Num(V("holders"), "#,0")} seat holders were not analysed (row cap); the return covers analysed seats only.";
                case CopilotInvestmentTrustCodes.AssumptionsMissing: return $"{Num(V("missing"), "0")} assumptions are missing.";
                case CopilotInvestmentTrustCodes.AssumptionsUnavailable: return "The saved assumptions could not be read.";
                case CopilotInvestmentTrustCodes.ScenarioApplied: return "Includes the reader's own what-if figures, which are not saved.";
                case CopilotInvestmentTrustCodes.SeatsCurrentAssignments: return "Seat costs assume current assignments were held for the whole period; licence history is not available for every holder.";
                case CopilotInvestmentTrustCodes.BalancesUnverified: return "Credit balances' accounting period is unverified, so they are excluded from cost and return.";
                case CopilotInvestmentTrustCodes.CreditCoverage: return $"The latest credit sweep observed {Pct(V("coverage"))} of directory users.";
                case CopilotInvestmentTrustCodes.CreditsStale: return $"The newest credit observation is {Num(V("days"), "0")} days older than the period end.";
                case CopilotInvestmentTrustCodes.CreditsPartial: return $"{Pct(V("share"))} of credit observations are partial.";
                case CopilotInvestmentTrustCodes.CreditsUnresolved: return $"{Num(V("credits"), "#,0.##")} credits sit on {Num(V("objects"), "#,0")} directory objects not matched to an imported user.";
                case CopilotInvestmentTrustCodes.CreditsMissing: return "No Copilot credit balances are available for this period.";
                case CopilotInvestmentTrustCodes.ShortWindow: return $"A {Num(V("days"), "0")}-day period: monthly figures are extrapolated.";
                case CopilotInvestmentTrustCodes.StudioImportFailed: return "The latest Copilot Studio per-user credit import reported an error.";
                default: return reason.Code;
            }
        }
    }
}

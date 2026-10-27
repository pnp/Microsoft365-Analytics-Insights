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
                WritePath(workbook, summary);
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
            sheet.AddRow("Of whom analysed in this period", XlsxCell.Number(seats.AnalysedHolders), "people", "The population value, cost and return are measured on");
            if (seats.OtherPeriodHolders > 0)
                sheet.AddRow("Of whom measured only by Microsoft's usage report", XlsxCell.Number(seats.OtherPeriodHolders), "people", "Its own period, not this one: counted as seats, left out of value and return");
            if (seats.NotAnalysedHolders > 0)
                sheet.AddRow("Of whom not analysed", XlsxCell.Number(seats.NotAnalysedHolders), "people", "Beyond the adoption analysis' limit: counted as seats, left out of value and return");
            sheet.AddRow("Seat run-rate per month", seats.MonthlyRunRate, currency, "All seat holders x seat price (modelled)");
            sheet.AddRow("Seat cost in the period", seats.WindowCost, currency, "Seat price x seat-days / average month (modelled)");
            sheet.AddRow("Modelled cost a month behind the return", summary.Investment.ModelledMonthlyCost, currency, "Analysed holders: seats plus their own billed Copilot Studio credits");
            sheet.AddRow("   of which seats", summary.Investment.MonthlySeatCost, currency, "Analysed holders' seats");
            sheet.AddRow("   of which Copilot Studio credits", summary.Investment.MonthlyStudioCost, currency, "Analysed holders' own billed credits at the Studio price");
            sheet.AddRow("Seat holders with Copilot activity", XlsxCell.Number(summary.Evidence.ActiveHolders), "people", "At least one active day in the period");
            sheet.AddRow("Active share of analysed seats", summary.Evidence.ActiveShare.HasValue ? (object)XlsxCell.Percent(summary.Evidence.ActiveShare.Value) : null, "%", "Observed");
            sheet.AddRow("Median active days (active holders)", summary.Evidence.MedianActiveDays, "days", "Observed");
            sheet.AddRow("Seat cost per active seat holder per month", seats.CostPerActiveHolderMonthly, currency, "Analysed holders' seat cost a month / those of them with Copilot activity (seats only, modelled)");
            foreach (var scenario in summary.Value.Scenarios)
            {
                sheet.AddRow("Modelled value per month (" + scenario.Scenario + ")", scenario.MonthlyValue, currency,
                    string.Format(CultureInfo.InvariantCulture, "{0:0.##} min per active day x {1:0%} realised x hourly cost", scenario.MinutesPerActiveDay, scenario.Realisation));
                sheet.AddRow("Return multiple (" + scenario.Scenario + ")", scenario.RoiMultiple, "x", "Modelled value / modelled cost of the same seat holders");
            }
            if (summary.Path.Available)
                sheet.AddRow("Shortfall (+) or surplus (-) a month, base", summary.Path.MonthlyGap, currency, "Modelled cost - base modelled value, a month");
            sheet.AddRow("Break-even minutes per active day", summary.BreakEven.MinutesPerActiveDay, "minutes", "At base realisation and hourly cost");
            sheet.AddRow("Margin of safety", summary.BreakEven.MarginOfSafety.HasValue ? (object)XlsxCell.Percent(summary.BreakEven.MarginOfSafety.Value) : null, "%", "1 - break-even minutes / base minutes");
            sheet.AddRow("Active days a month one seat needs", summary.BreakEven.ActiveDaysPerMonthPerSeat, "days", "Base assumptions");
            sheet.AddRow("Copilot Studio billed credits in the period", summary.Investment.Studio.Credits, "credits", "Daily ledger, attributed to people");
            sheet.AddRow("Copilot Studio credits, previous period", summary.Investment.Studio.PriorCredits, "credits",
                summary.Investment.Studio.PriorCredits.HasValue ? "Same ledger, equal-length period before" : "Not compared: the ledger does not cover the whole previous period");
            sheet.AddRow("Latest preview credit balances", summary.Investment.Credits.Credits, "credits", "Balance in the source's own period (unverified); not spend");
            sheet.AddRow("Balance valued at the credit price", summary.Investment.Credits.BalanceValue, currency, "A valuation of a balance, not a period cost");
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Finding", "Tone", "", "Detail");
            foreach (var finding in summary.Findings) sheet.AddRow(finding.Code, finding.Tone, null, XlsxCell.Wrapped(FindingText(finding, currency)));
            sheet.AddBlankRow();

            sheet.AddHeaderRow("Recommended action", "People", "Amount per month", "What it means");
            foreach (var action in summary.Actions)
                sheet.AddRow(action.Code, XlsxCell.Number(action.People), action.MonthlyAmount, XlsxCell.Wrapped(EffectText(action.Effect) + ActionText(action, currency)));
        }

        /// <summary>What an action's amount is, so amounts of different kinds are never read as one column of savings.</summary>
        private static string EffectText(string effect)
        {
            switch (effect)
            {
                case CopilotInvestmentActionEffects.FreesSeatCost: return "[Amount: seat cost freed] ";
                case CopilotInvestmentActionEffects.IdleSeatCost: return "[Amount: seat cost held by idle seats] ";
                case CopilotInvestmentActionEffects.ValueShortfall: return "[Amount: modelled value still missing] ";
                case CopilotInvestmentActionEffects.AddsSeatCost: return "[Amount: seat cost added] ";
                case CopilotInvestmentActionEffects.BalanceIfMonthly: return "[Amount: balances valued as if a month's spend] ";
                case CopilotInvestmentActionEffects.MeteredSpend: return "[Amount: billed Studio credits a month] ";
                default: return string.Empty;
            }
        }

        private static void WriteValue(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Value and break-even");
            sheet.SetColumnWidths(18, 16, 14, 16, 16, 18, 18, 14, 18, 22);
            sheet.AddTitle("Modelled value, break-even and sensitivity");
            if (!summary.Value.Available)
            {
                sheet.AddRow(summary.Value.UnavailableReason == CopilotInvestmentValueUnavailable.NoMatchedHolders
                    ? "Modelled value is unavailable: no seat holder's Copilot use was measured in this period (only Microsoft's usage report, for its own period, or none)."
                    : "Modelled value is unavailable until these assumptions are set: " + string.Join(", ", summary.Value.Missing));
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
            sheet.AddRow("Base return without the seats that are safe to reclaim (seat cost only; Studio use stays billed)", summary.Sensitivity.RoiBaseAfterReclaim);
            sheet.AddRow("Stress floor: base return IF each latest credit balance were an extra month of spend (unverified; may overlap the Studio ledger)", summary.Sensitivity.RoiBaseIfBalancesMonthly);
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Active days per month, from", "to", "Seat holders", "Below the seat-only break-even");
            foreach (var bucket in summary.Evidence.Distribution)
                sheet.AddRow(bucket.Key == "none" ? "No use" : (object)bucket.MinDaysPerMonth, bucket.Key == "none" ? null : (object)bucket.MaxDaysPerMonth,
                    XlsxCell.Number(bucket.Holders), bucket.BelowBreakEven);
        }

        private static void WriteCohorts(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Adoption cohorts");
            sheet.SetColumnWidths(16, 12, 14, 14, 14, 16, 16, 16, 14, 14, 14);
            sheet.AddHeaderRow("Adoption band", "Seat holders", "Share of seats", "Active days", "Share of active days", "Median active days", "Modelled cost (in period)", "Modelled value, base (in period)", "Return (base)", "Credits (latest balance)", "Studio credits (in period)");
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
            sheet.AddHeaderRow("Group", "Seat holders", "Active", "Idle", "Active share", "Active days per holder per month", "Modelled cost (in period)", "Modelled value, base (in period)", "Return (base)", "Credits (latest balance)", "Credit consumers", "Studio credits (in period)", "Unlicensed Copilot users");
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
            sheet.AddRow("Billed credits, previous period of the same length", studio.PriorCredits.HasValue ? (object)studio.PriorCredits.Value : "Not compared: the ledger does not cover the whole previous period");
            sheet.AddRow("Of which seat holders", studio.HolderCredits);
            sheet.AddRow("Valued at the Studio credit price", studio.WindowValue);
            if (summary.Sources.Studio.FailureCode != null)
                sheet.AddRow("The Copilot Studio ledger could not be read for this workbook; its figures are missing, not zero.");
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
            sheet.AddRow("Return multiple", null, null, "Modelled value / modelled cost, for the same analysed seat holders. Cost adds their own billed Copilot Studio credits when a Studio price is set. Seat holders whose use is known only from Microsoft's usage report (its own period) are counted as seats but left out of value and return.");
            sheet.AddRow("Break-even minutes per active day", null, null, "Modelled cost / (total active days x base realisation x hourly cost / 60).");
            sheet.AddRow("Active days a month one seat needs", null, null, "Seat price / (base minutes / 60 x base realisation x hourly cost). A person's own need adds their billed Studio credits to the seat price.");
            sheet.AddRow("Extra active days to break even", null, null, "(Monthly modelled cost - monthly base value) / value of one active day.");
            sheet.AddRow("Credit balances", null, null, "Latest observation per person; never summed across days, never treated as spend, and excluded from the return.");
            sheet.AddRow("Concentration", null, null, "Share of the total held by the top 20% of seat holders (active days) or top 10% of consumers (credits); Gini coefficient over the same population.");
            sheet.AddRow("Modelled value is not a saving", null, null, "It is assumed time back on observed active days, valued at the hourly cost. Whether it becomes savings or output depends on how that time is used; it is not measured here.");
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Confidence", "Level", "", "");
            foreach (var area in summary.Trust.Areas) sheet.AddRow(area.Area, area.Level);
            sheet.AddBlankRow();
            sheet.AddHeaderRow("Confidence reason", "Impact", "Area", "Detail");
            foreach (var reason in summary.Trust.Reasons) sheet.AddRow(reason.Code, reason.Impact, reason.Area, XlsxCell.Wrapped(TrustText(reason)));
        }

        private static void WritePath(XlsxWriter workbook, CopilotInvestmentSummary summary)
        {
            var sheet = workbook.AddSheet("Path and concentration");
            sheet.SetColumnWidths(58, 18, 18, 18, 18);
            var currency = Currency(summary);
            var path = summary.Path;
            sheet.AddTitle("What it would take, where it is concentrated, and the people without a seat");
            sheet.AddHeaderRow("Path to break-even (base assumptions, a month)", "Value", "Unit");
            if (!path.Available)
            {
                sheet.AddRow("Not modelled: value needs the assumptions and seat holders whose use was measured in this period.");
            }
            else
            {
                sheet.AddRow("Modelled cost a month", path.MonthlyCost, currency);
                sheet.AddRow("Modelled value a month (base)", path.MonthlyValueBase, currency);
                sheet.AddRow("Shortfall (positive) or surplus (negative) a month", path.MonthlyGap, currency);
                sheet.AddRow("Value of one active day", path.ValuePerActiveDay, currency);
                sheet.AddRow("Extra active seat-days a month to break even", path.ExtraActiveDaysPerMonth, "days");
                sheet.AddRow("The same, per seat holder already using Copilot", path.ExtraDaysPerActiveHolder, "days");
                sheet.AddRow("Minutes per active day needed / assumed", Num(path.MinutesNeeded) + " / " + Num(path.MinutesAssumed), "minutes");
                sheet.AddRow("Idle seats safe to reclaim", XlsxCell.Number(path.Reclaimable), "seats");
                sheet.AddRow("Seat cost they free a month (a saving only where seats can be reduced)", path.ReclaimMonthly, currency);
                sheet.AddRow("Base return without them", path.RoiAfterReclaim, "x");
                sheet.AddRow("People without a seat active on enough days to pay for one", XlsxCell.Number(path.EvidencedWithoutSeat), "people");
                sheet.AddRow("Of whom reclaimed seats could cover", XlsxCell.Number(path.Reassignable), "people");
                sheet.AddRow("Seats still to buy after reassigning", XlsxCell.Number(path.NetNewSeats), "seats");
                sheet.AddRow("Their seat cost a month", path.NetNewMonthly, currency);
            }
            sheet.AddBlankRow();

            var concentration = summary.Concentration;
            sheet.AddHeaderRow("Concentration", "Population", "Top share", "Of the top", "Gini");
            if (concentration.ActiveDays != null)
                sheet.AddRow("Active days among analysed seat holders", XlsxCell.Number(concentration.ActiveDays.Population),
                    XlsxCell.Percent(concentration.ActiveDays.TopShare), XlsxCell.Percent(concentration.ActiveDays.TopFraction), concentration.ActiveDays.Gini);
            if (concentration.Credits != null)
                sheet.AddRow("Latest credit balances among people with one", XlsxCell.Number(concentration.Credits.Population),
                    XlsxCell.Percent(concentration.Credits.TopShare), XlsxCell.Percent(concentration.Credits.TopFraction), concentration.Credits.Gini);
            sheet.AddBlankRow();
            if (concentration.Quadrants.Count > 0)
            {
                sheet.AddRow("Credit balance against own use: split at the median balance (" + Num(concentration.QuadrantMedianCredits) + " credits) and at "
                    + Num(concentration.QuadrantDaysPerMonth) + " active days a month.");
                sheet.AddHeaderRow("Group", "People", "Credits (latest balance)");
                foreach (var q in concentration.Quadrants) sheet.AddRow(QuadrantText(q.Key), XlsxCell.Number(q.People), q.Credits);
                sheet.AddBlankRow();
            }

            var metered = summary.Metered;
            sheet.AddHeaderRow("People without a seat", "Value", "Unit");
            sheet.AddRow("People in scope without a seat with any Copilot activity, balance or billing", XlsxCell.Number(metered.People), "people");
            sheet.AddRow("Of whom active in Copilot", XlsxCell.Number(metered.ActiveUsers), "people");
            sheet.AddRow("Their active days", XlsxCell.Number(metered.ActiveDays), "days");
            sheet.AddRow("Billed Copilot Studio credits", metered.StudioCredits, "credits");
            sheet.AddRow("Valued at the Studio credit price", metered.StudioWindowValue, currency);
            sheet.AddRow("Latest credit balances", metered.Credits, "credits");
            sheet.AddRow("People whose Studio billing already costs a seat a month", metered.StudioAboveSeatPrice, "people");
            sheet.AddRow("People active on enough days to pay for a seat", metered.EvidencedCandidates, "people");
            sheet.AddRow("Recommended for a seat by the adoption analysis", XlsxCell.Number(metered.RecommendedCandidates), "people");
            sheet.AddBlankRow();

            var trajectory = summary.Trajectory;
            sheet.AddHeaderRow("Week starting (UTC)", "Active seat holders", "Share of today's seats");
            if (!trajectory.Available)
                sheet.AddRow(trajectory.UnavailableReason == CopilotInvestmentTrajectoryUnavailable.Filtered
                    ? "The weekly trend is tenant-wide, so it is left out of a filtered workbook." : "No weekly trend is available.");
            foreach (var week in trajectory.Weeks)
                sheet.AddRow(XlsxCell.Date(week.WeekStart), week.ActiveHolders, week.Share.HasValue ? (object)XlsxCell.Percent(week.Share.Value) : null);
        }

        private static string QuadrantText(string key)
        {
            switch (key)
            {
                case CopilotInvestmentCalculator.QuadrantKeys.HighSpendStrongEvidence: return "Higher balance, regular use";
                case CopilotInvestmentCalculator.QuadrantKeys.HighSpendThinEvidence: return "Higher balance, light use";
                case CopilotInvestmentCalculator.QuadrantKeys.LowSpendStrongEvidence: return "Lower balance, regular use";
                case CopilotInvestmentCalculator.QuadrantKeys.LowSpendThinEvidence: return "Lower balance, light use";
                default: return key;
            }
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
                "Value (upside)", "Value to cost (base)", "Active days a month its own cost needs", "Extra active days a month to break even",
                "Investment signal", "Flags",
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
                    p.BreakEvenDaysPerMonth, p.BreakEvenGapDaysPerMonth, p.Signal, string.Join(", ", p.Flags));
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

        private static string Noun(double? count, string one, string many) => count.HasValue && count.Value == 1 ? one : many;

        private static string Pct(double? value) => value.HasValue ? (value.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "-";

        internal static string FindingText(CopilotInvestmentFinding finding, string currency)
        {
            double? V(string key) => finding.Values.TryGetValue(key, out var v) ? v : null;
            switch (finding.Code)
            {
                case CopilotInvestmentFindingCodes.RunRate:
                    return $"Seats cost {Num(V("monthly"), "#,0.##")} {currency} a month (modelled) for {Num(V("holders"), "#,0")} {Noun(V("holders"), "seat holder", "seat holders")}; {Num(V("costPerActive"), "#,0.##")} {currency} per seat holder who used Copilot.";
                case CopilotInvestmentFindingCodes.ValueRange:
                    return $"On these assumptions, the time Copilot gives back on observed active days is worth {Num(V("low"), "#,0")} to {Num(V("high"), "#,0")} {currency} a month (base {Num(V("base"), "#,0")}) against {Num(V("cost"), "#,0")} {currency} of modelled cost: a return of {Num(V("roiLow"))}x to {Num(V("roiHigh"))}x (base {Num(V("roiBase"))}x). An assumed time value, not a measured saving.";
                case CopilotInvestmentFindingCodes.BreakEven:
                    return $"The seats break even at {Num(V("minutes"))} minutes per active day against {Num(V("assumed"))} assumed (margin of safety {Pct(V("margin"))}); one seat needs about {Num(V("daysPerMonth"))} active days a month. {Pct(V("shareAtOrAbove"))} of analysed seats already reach it.";
                case CopilotInvestmentFindingCodes.IdleSeats:
                    return $"Seat holders who used Copilot on no day in the period: {Num(V("idle"), "#,0")} ({Pct(V("share"))}). Safe to reclaim: {Num(V("reclaimable"), "#,0")} (freeing {Num(V("monthly"), "#,0.##")} {currency} a month of seat cost); to review: {Num(V("review"), "#,0")}; too new to judge: {Num(V("tooNew"), "#,0")}.";
                case CopilotInvestmentFindingCodes.EvidenceConcentration:
                    return $"The most active {Pct(V("topFraction"))} of seat holders account for {Pct(V("topShare"))} of all Copilot active days (Gini {Num(V("gini"))}).";
                case CopilotInvestmentFindingCodes.CreditConcentration:
                    return $"The top {Pct(V("topFraction"))} of {Num(V("consumers"), "#,0")} credit consumers hold {Pct(V("topShare"))} of the latest balances.";
                case CopilotInvestmentFindingCodes.HighSpendThinEvidence:
                    return $"People among the largest credit balances who show thin Copilot activity: {Num(V("people"), "#,0")} ({Num(V("credits"), "#,0.##")} credits). Review what drives that consumption.";
                case CopilotInvestmentFindingCodes.ExpandCandidates:
                    return $"People without a seat whose Copilot use already justifies one: {Num(V("people"), "#,0")}. Licensing them would cost {Num(V("monthly"), "#,0.##")} {currency} a month (modelled).";
                case CopilotInvestmentFindingCodes.LimitBlocked:
                    return $"People who have exhausted a per-user credit limit: {Num(V("people"), "#,0")}, of whom {Num(V("evidenced"), "#,0")} show strong Copilot activity.";
                case CopilotInvestmentFindingCodes.AssumptionsMissing:
                    return $"Missing assumptions: {Num(V("missing"), "0")}. Money, return and break-even are not modelled.";
                case CopilotInvestmentFindingCodes.BalancesUnverified:
                    return $"Latest balances hold {Num(V("credits"), "#,0.##")} credits across {Num(V("consumers"), "#,0")} consumers. The source does not identify their period, so they are not counted as spend."
                        + (V("roiIfMonthly").HasValue ? $" Stress floor: if each were an extra month of spend on top of everything else, the base return would be {Num(V("roiIfMonthly"))}x (they may overlap the Studio ledger)." : string.Empty);
                case CopilotInvestmentFindingCodes.StudioTrend:
                    return V("prior").HasValue
                        ? $"Copilot Studio billed {Num(V("credits"), "#,0.##")} credits in the period against {Num(V("prior"), "#,0.##")} in the equal period before ({Pct(V("change"))})."
                        : $"Copilot Studio billed {Num(V("credits"), "#,0.##")} credits in the period. The ledger does not cover the whole previous period, so no comparison is made.";
                case CopilotInvestmentFindingCodes.Trajectory:
                    return $"Weekly active seat holders went from {Num(V("first"), "#,0")} to {Num(V("last"), "#,0")} over the last {Num(V("weeks"), "0")} measured weeks ({Pct(V("change"))}); {Pct(V("share"))} of today's seats in the latest week. Tenant-wide.";
                case CopilotInvestmentFindingCodes.NoMatchedHolders:
                    return $"No seat holder's Copilot use was measured in this period, so value and return are not modelled. Known only from Microsoft's usage report (its own period): {Num(V("otherPeriod"), "#,0")}; not analysed: {Num(V("notAnalysed"), "#,0")}.";
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
                case CopilotInvestmentActionCodes.ReclaimIdle: return $"Reclaim seats with no Copilot activity that the adoption report rates certain or probable to reclaim. This frees seat cost; it is a cash saving only where seats can be reduced. Base return without them: {Num(action.RoiBaseAfter)}x.";
                case CopilotInvestmentActionCodes.ReviewIdle: return "Review seats with no Copilot activity that the adoption rules do not mark safe to reclaim: ask before reassigning.";
                case CopilotInvestmentActionCodes.EnableBelowBreakEven: return $"Support seat holders who use Copilot but below their own break-even; the median holder needs about {Num(action.Metric)} more active days a month. The amount is the modelled value still missing, not a cost.";
                case CopilotInvestmentActionCodes.EnableLightEvidence: return "Coach seat holders with only light Copilot activity.";
                case CopilotInvestmentActionCodes.ExpandEvidenced: return $"Offer seats to people without one whose Copilot use already exceeds the {Num(action.Metric)} active days a month a seat needs. The amount is the seat cost it would add; their use shows demand, not the gain a seat would bring.";
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
                case CopilotInvestmentTrustCodes.UsageReportSignal: return $"Seat holders whose use is known only from Microsoft's usage report, for its own period: {Num(V("holders"), "#,0")}. They count as seats but are left out of value and return.";
                case CopilotInvestmentTrustCodes.FiguresIncomplete: return "Some adoption queries failed, so figures are incomplete.";
                case CopilotInvestmentTrustCodes.HoldersNotAnalysed: return $"Seat holders not analysed (row cap): {Num(V("holders"), "#,0")}. The return covers analysed seats only.";
                case CopilotInvestmentTrustCodes.AssumptionsMissing: return $"Missing assumptions: {Num(V("missing"), "0")}.";
                case CopilotInvestmentTrustCodes.AssumptionsUnavailable: return "The saved assumptions could not be read.";
                case CopilotInvestmentTrustCodes.ScenarioApplied: return "Includes the reader's own what-if figures, which are not saved.";
                case CopilotInvestmentTrustCodes.AssumedTimeValue: return "Value is assumed time back on observed active days, valued at the hourly cost: whether it becomes savings depends on how the time is used.";
                case CopilotInvestmentTrustCodes.SeatsCurrentAssignments: return "Seat costs assume current assignments were held for the whole period; licence history is not available for every holder.";
                case CopilotInvestmentTrustCodes.BalancesUnverified: return "Credit balances' accounting period is unverified, so they are excluded from cost and return.";
                case CopilotInvestmentTrustCodes.CreditCoverage: return $"The latest credit sweep observed {Pct(V("coverage"))} of directory users.";
                case CopilotInvestmentTrustCodes.CreditsStale: return $"The newest credit observation is {Num(V("days"), "0")} days older than the period end.";
                case CopilotInvestmentTrustCodes.CreditsPartial: return $"{Pct(V("share"))} of credit observations are partial.";
                case CopilotInvestmentTrustCodes.CreditsUnresolved: return $"{Num(V("credits"), "#,0.##")} credits sit on {Num(V("objects"), "#,0")} directory objects not matched to an imported user.";
                case CopilotInvestmentTrustCodes.CreditsMissing: return "No Copilot credit balances are available for this period.";
                case CopilotInvestmentTrustCodes.CreditsUnavailable: return "Copilot credit balances could not be read just now; they are missing, not zero.";
                case CopilotInvestmentTrustCodes.ShortWindow: return $"A {Num(V("days"), "0")}-day period: monthly figures are extrapolated.";
                case CopilotInvestmentTrustCodes.StudioImportFailed: return "The latest Copilot Studio per-user credit import reported an error.";
                case CopilotInvestmentTrustCodes.StudioUnavailable: return "The Copilot Studio ledger could not be read, so the cost behind the return covers seats only and the return may be overstated.";
                default: return reason.Code;
            }
        }
    }
}

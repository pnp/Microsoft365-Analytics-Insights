using Common.Entities.Localisation;
using Common.Entities.Xlsx;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    public static partial class CopilotInvestmentWorkbook
    {
        private static void WriteValue(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.value"));
            sheet.SetColumnWidths(18, 16, 14, 16, 16, 18, 18, 14, 18, 22);
            sheet.AddTitle(words.W("value.title"));
            if (!summary.Value.Available)
            {
                sheet.AddRow(summary.Value.UnavailableReason == CopilotInvestmentValueUnavailable.NoMatchedHolders
                    ? words.W("value.unmatched")
                    : words.W("value.missing", ("fields", FieldList(summary.Value.Missing, words))));
                return;
            }
            sheet.AddHeaderRow(words.T("copilotInvestment.value.col.scenario"), words.T("copilotInvestment.value.col.minutes"),
                words.T("copilotInvestment.value.col.realisation"), words.T("copilotInvestment.value.col.assistedHours"),
                words.T("copilotInvestment.value.col.realisedHours"), words.T("copilotInvestment.value.col.value"),
                words.T("copilotInvestment.value.col.monthly"), words.T("copilotInvestment.value.col.roi"),
                words.T("copilotInvestment.value.col.net"), words.T("copilotInvestment.value.col.atOrAbove"));
            foreach (var s in summary.Value.Scenarios)
                sheet.AddRow(words.Label(Words.Scenario, s.Scenario), s.MinutesPerActiveDay, XlsxCell.Percent(s.Realisation), s.AssistedHours, s.RealisedHours,
                    s.Value, s.MonthlyValue, s.RoiMultiple, s.Net, XlsxCell.Number(s.HoldersAtOrAboveBreakEven));
            sheet.AddBlankRow();
            var b = summary.BreakEven;
            sheet.AddHeaderRow(words.W("value.breakEven"), words.W("col.value"));
            sheet.AddRow(words.W("value.minutesNeeded"), b.MinutesPerActiveDay);
            sheet.AddRow(words.W("value.marginBase"), b.MarginOfSafety.HasValue ? (object)XlsxCell.Percent(b.MarginOfSafety.Value) : null);
            sheet.AddRow(words.W("exec.seatDays"), b.ActiveDaysPerMonthPerSeat);
            sheet.AddRow(words.W("value.minutesPerWeekday"), b.MinutesPerWeekdayPerSeat);
            sheet.AddRow(words.W("value.atOrAbove"), XlsxCell.Number(b.HoldersAtOrAbove));
            sheet.AddRow(words.W("value.below"), XlsxCell.Number(b.HoldersBelow));
            sheet.AddRow(words.W("value.noActivity"), XlsxCell.Number(b.HoldersNoEvidence));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("value.col.minutesScenario"), words.W("value.col.realisationScenario"), words.T("copilotInvestment.value.col.value"),
                words.T("copilotInvestment.value.col.roi"));
            foreach (var cell in summary.Sensitivity.Grid)
                sheet.AddRow(words.Label(Words.Scenario, cell.MinutesScenario), words.Label(Words.Scenario, cell.RealisationScenario), cell.Value, cell.RoiMultiple);
            sheet.AddBlankRow();
            sheet.AddRow(words.W("value.hourlyLower"), summary.Sensitivity.RoiBaseHourlyMinus20);
            sheet.AddRow(words.W("value.hourlyHigher"), summary.Sensitivity.RoiBaseHourlyPlus20);
            sheet.AddRow(words.W("value.afterReclaim"), summary.Sensitivity.RoiBaseAfterReclaim);
            sheet.AddRow(words.W("value.stressFloor"), summary.Sensitivity.RoiBaseIfBalancesMonthly);
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("value.col.from"), words.W("value.col.to"), words.W("value.col.holders"), words.W("value.col.belowSeat"));
            foreach (var bucket in summary.Evidence.Distribution)
                sheet.AddRow(bucket.Key == "none" ? words.T("copilotInvestment.exec.distribution.bucket.none") : (object)bucket.MinDaysPerMonth,
                    bucket.Key == "none" ? null : (object)bucket.MaxDaysPerMonth, XlsxCell.Number(bucket.Holders), bucket.BelowBreakEven);
        }

        private static void WritePath(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.path"));
            sheet.SetColumnWidths(58, 18, 18, 18, 18);
            var currency = Currency(summary, words);
            var days = words.W("unit.days");
            var people = words.W("unit.people");
            var seats = words.W("unit.seats");
            var path = summary.Path;
            sheet.AddTitle(words.W("path.title"));
            sheet.AddHeaderRow(words.W("path.col.path"), words.W("col.value"), words.W("col.unit"));
            if (!path.Available)
            {
                sheet.AddRow(words.W("path.notModelled"));
            }
            else
            {
                sheet.AddRow(words.W("path.cost"), path.MonthlyCost, currency);
                sheet.AddRow(words.W("path.value"), path.MonthlyValueBase, currency);
                sheet.AddRow(words.W("path.gap"), path.MonthlyGap, currency);
                sheet.AddRow(words.W("path.dayValue"), path.ValuePerActiveDay, currency);
                sheet.AddRow(words.W("path.extraDays"), path.ExtraActiveDaysPerMonth, days);
                sheet.AddRow(words.W("path.extraPerHolder"), path.ExtraDaysPerActiveHolder, days);
                sheet.AddRow(words.W("path.minutes"), words.Num(path.MinutesNeeded) + " / " + words.Num(path.MinutesAssumed), words.W("unit.minutes"));
                sheet.AddRow(words.W("path.reclaimable"), XlsxCell.Number(path.Reclaimable), seats);
                sheet.AddRow(words.W("path.reclaimMonthly"), path.ReclaimMonthly, currency);
                sheet.AddRow(words.W("path.roiAfter"), path.RoiAfterReclaim, MultipleUnit);
                sheet.AddRow(words.W("path.evidenced"), XlsxCell.Number(path.EvidencedWithoutSeat), people);
                sheet.AddRow(words.W("path.reassignable"), XlsxCell.Number(path.Reassignable), people);
                sheet.AddRow(words.W("path.netNew"), XlsxCell.Number(path.NetNewSeats), seats);
                sheet.AddRow(words.W("path.netNewMonthly"), path.NetNewMonthly, currency);
            }
            sheet.AddBlankRow();

            var concentration = summary.Concentration;
            sheet.AddHeaderRow(words.W("path.col.concentration"), words.W("path.col.population"), words.W("path.col.topShare"), words.W("path.col.topFraction"), Gini);
            if (concentration.ActiveDays != null)
                sheet.AddRow(words.W("path.activeDays"), XlsxCell.Number(concentration.ActiveDays.Population),
                    XlsxCell.Percent(concentration.ActiveDays.TopShare), XlsxCell.Percent(concentration.ActiveDays.TopFraction), concentration.ActiveDays.Gini);
            if (concentration.Credits != null)
                sheet.AddRow(words.W("path.balances"), XlsxCell.Number(concentration.Credits.Population),
                    XlsxCell.Percent(concentration.Credits.TopShare), XlsxCell.Percent(concentration.Credits.TopFraction), concentration.Credits.Gini);
            sheet.AddBlankRow();
            if (concentration.Quadrants.Count > 0)
            {
                sheet.AddRow(XlsxCell.Wrapped(words.T("copilotInvestment.quadrants.description",
                    ("median", words.Num(concentration.QuadrantMedianCredits)), ("days", words.Num(concentration.QuadrantDaysPerMonth)))));
                sheet.AddHeaderRow(words.W("col.group"), words.W("col.people"), words.W("col.credits"));
                foreach (var q in concentration.Quadrants) sheet.AddRow(QuadrantLabel(q.Key, words), XlsxCell.Number(q.People), q.Credits);
                sheet.AddBlankRow();
            }

            var metered = summary.Metered;
            sheet.AddHeaderRow(words.W("path.col.withoutSeat"), words.W("col.value"), words.W("col.unit"));
            sheet.AddRow(words.W("path.metered.people"), XlsxCell.Number(metered.People), people);
            sheet.AddRow(words.W("path.metered.active"), XlsxCell.Number(metered.ActiveUsers), people);
            sheet.AddRow(words.W("path.metered.days"), XlsxCell.Number(metered.ActiveDays), days);
            sheet.AddRow(words.W("path.metered.studio"), metered.StudioCredits, words.W("unit.credits"));
            sheet.AddRow(words.W("studio.valued"), metered.StudioWindowValue, currency);
            sheet.AddRow(words.W("path.metered.balances"), metered.Credits, words.W("unit.credits"));
            sheet.AddRow(words.W("path.metered.aboveSeat"), metered.StudioAboveSeatPrice, people);
            sheet.AddRow(words.W("path.metered.evidenced"), metered.EvidencedCandidates, people);
            sheet.AddRow(words.W("path.metered.recommended"), XlsxCell.Number(metered.RecommendedCandidates), people);
            sheet.AddBlankRow();

            var trajectory = summary.Trajectory;
            sheet.AddHeaderRow(words.W("path.col.week"), words.W("path.col.activeHolders"), words.W("path.col.shareOfSeats"));
            if (!trajectory.Available)
                sheet.AddRow(trajectory.UnavailableReason == CopilotInvestmentTrajectoryUnavailable.Filtered ? words.W("path.trendFiltered") : words.W("path.trendNone"));
            foreach (var week in trajectory.Weeks)
                sheet.AddRow(XlsxCell.Date(week.WeekStart), week.ActiveHolders, week.Share.HasValue ? (object)XlsxCell.Percent(week.Share.Value) : null);
        }

        /// <summary>The statistic's own name, the same in every language the portal ships.</summary>
        private const string Gini = "Gini";

        private static void WriteCohorts(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.cohorts"));
            sheet.SetColumnWidths(16, 12, 14, 14, 14, 16, 16, 16, 14, 14, 14);
            sheet.AddHeaderRow(words.T("copilotInvestment.value.col.band"), words.W("value.col.holders"), words.W("cohort.col.shareOfSeats"),
                words.W("col.activeDays"), words.T("copilotInvestment.value.col.shareOfDays"), words.T("copilotInvestment.value.col.medianDays"),
                words.W("col.costPeriod"), words.W("col.valuePeriod"), words.W("col.roiBase"), words.W("col.credits"), words.W("col.studioPeriod"));
            foreach (var c in summary.Cohorts)
                sheet.AddRow(words.Label(Words.Band, c.Band), XlsxCell.Number(c.Holders), c.ShareOfHolders.HasValue ? (object)XlsxCell.Percent(c.ShareOfHolders.Value) : null,
                    XlsxCell.Number(c.ActiveDays), c.ShareOfActiveDays.HasValue ? (object)XlsxCell.Percent(c.ShareOfActiveDays.Value) : null,
                    c.MedianActiveDays, c.Cost, c.ValueBase, c.RoiBase, c.Credits, c.StudioCredits);
            sheet.FreezeTopRows(1);
        }

        private static void WriteOrganisation(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.organisation"));
            var organisation = summary.Organisation;
            sheet.SetColumnWidths(34, 12, 12, 12, 12, 16, 14, 16, 12, 14, 14, 14, 16);
            sheet.AddRow(words.W("org.groupedBy"), FilterWording.DimensionLabel(organisation.Dimension, organisation.DimensionName, words.Language), null,
                words.W("org.minGroup", ("count", words.Count(organisation.MinGroupSize))));
            if (organisation.Hidden)
            {
                sheet.AddRow(words.W("org.hidden"));
                return;
            }
            sheet.AddHeaderRow(words.W("col.group"), words.W("value.col.holders"), words.W("org.col.active"), words.W("org.col.idle"), words.W("org.col.activeShare"),
                words.W("org.col.daysPerHolder"), words.W("col.costPeriod"), words.W("col.valuePeriod"), words.W("col.roiBase"), words.W("col.credits"),
                words.W("org.col.consumers"), words.W("col.studioPeriod"), words.W("org.col.unlicensed"));
            var first = sheet.CurrentRow;
            foreach (var g in organisation.Groups)
            {
                // A combination of too few people shows no figures at all, only why - never zeros that read as measurements.
                if (g.Withheld)
                {
                    sheet.AddRow(words.W("org.combinedWithheld", ("count", words.Count(organisation.MinGroupSize))));
                    continue;
                }
                sheet.AddRow(g.Label ?? (g.EmptyKey == CopilotInvestmentCalculator.GroupKeys.NotSet ? words.W("org.notSet") : words.W("org.combined")),
                    XlsxCell.Number(g.Holders), XlsxCell.Number(g.ActiveHolders), XlsxCell.Number(g.IdleHolders),
                    g.ActiveShare.HasValue ? (object)XlsxCell.Percent(g.ActiveShare.Value) : null, g.ActiveDaysPerHolderMonthly,
                    g.Cost, g.ValueBase, g.RoiBase, g.Credits, XlsxCell.Number(g.CreditConsumers), g.StudioCredits, XlsxCell.Number(g.UnlicensedActiveUsers));
            }
            sheet.FreezeTopRows(first);
            if (sheet.CurrentRow > first) sheet.AddAutoFilter(first, sheet.CurrentRow, 1, 13);
        }

        private static void WriteCredits(XlsxWriter workbook, CopilotInvestmentSummary summary, Words words)
        {
            var sheet = workbook.AddSheet(words.W("sheet.credits"));
            sheet.SetColumnWidths(40, 18, 16, 18, 16, 16, 16, 30, 40);
            var source = summary.Sources.Credits;
            sheet.AddTitle(words.W("credits.title"));
            sheet.AddRow(XlsxCell.Wrapped(words.W("credits.intro")));
            sheet.AddRow(words.W("credits.snapshot"), source.SnapshotDate, null, words.W("credits.snapshotNote", ("days", words.Count(CopilotInvestmentSql.SnapshotBandDays))));
            sheet.AddRow(words.W("credits.first"), source.FirstObservationDate);
            sheet.AddRow(words.W("credits.periodStatus"), source.PeriodStatus == null ? words.W("credits.status.none") : words.Label(Words.PeriodStatus, source.PeriodStatus));
            sheet.AddRow(words.W("credits.observed"), XlsxCell.Number(source.ObservedUsers));
            sheet.AddRow(words.W("credits.partial"), XlsxCell.Number(source.PartialUsers));
            sheet.AddRow(words.W("credits.unresolvedObjects"), source.UnresolvedObjects);
            sheet.AddRow(words.W("credits.unresolvedCredits"), source.UnresolvedCredits);
            sheet.AddRow(words.W("credits.sweeps"), XlsxCell.Number(source.SweepsInWindow));
            sheet.AddRow(words.W("credits.sweepsCompleted"), XlsxCell.Number(source.CompletedSweepsInWindow));
            sheet.AddRow(words.W("credits.directoryUsers"), source.DirectoryUsers);
            sheet.AddRow(words.W("credits.collectorObserved"), source.CollectorObserved);
            sheet.AddRow(words.W("credits.collectorFailed"), source.CollectorFailed);
            sheet.AddRow(words.W("credits.collectorDenied"), source.CollectorDenied);
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("credits.col.service"), words.W("col.unit"), words.W("credits.col.consuming"), words.W("credits.col.quantity"),
                words.W("credits.col.unknownAmounts"), words.W("credits.col.limitExhausted"), words.W("credits.col.notEntitled"),
                words.T("copilotInvestment.services.col.unevaluated"), words.W("credits.col.policies"));
            foreach (var s in summary.Services)
                sheet.AddRow(s.Category, s.Unit ?? words.W("credits.unknownUnit"), XlsxCell.Number(s.Consumers), s.Quantity, XlsxCell.Number(s.UnknownAmountUsers),
                    XlsxCell.Number(s.LimitExhaustedUsers), XlsxCell.Number(s.NotEntitledUsers), XlsxCell.Number(s.UnevaluatedUsers), string.Join(", ", s.Policies));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("credits.col.policy"), words.W("credits.col.type"), words.W("credits.col.status"), words.W("credits.col.billing"),
                words.W("credits.col.periodLimit"), words.W("credits.col.perUserLimit"), words.W("credits.col.groups"), words.T("copilotInvestment.policies.col.services"));
            foreach (var p in summary.Policies)
                sheet.AddRow(p.DisplayName ?? p.Id, PolicyScope(p.Type, words), p.Status == null ? words.T("copilotInvestment.value.unknown") : words.Label(PolicyStatus, p.Status),
                    BillingMethods(p.BillingMethods, words), QuantityText(p.PeriodLimit, words), QuantityText(p.PerUserLimit, words),
                    p.AssignedGroups, string.Join(", ", p.Services.Where(s => s.State == EnabledService).Select(s => s.ServiceId)));
            sheet.AddBlankRow();
            sheet.AddHeaderRow(words.W("credits.col.tenantBalance"), words.W("credits.col.purchased"), words.W("credits.col.consumed"), words.W("credits.col.remaining"),
                words.W("col.unit"), words.W("credits.col.lastModified"));
            foreach (var t in summary.TenantBalances)
                sheet.AddRow(words.Label(BillingMethod, t.BillingMethod), t.Purchased, t.Consumed, t.Remaining, t.Unit, t.LastModified);
            sheet.AddBlankRow();
            var studio = summary.Investment.Studio;
            sheet.AddHeaderRow(words.W("credits.studio"), words.W("col.value"));
            sheet.AddRow(words.W("credits.studio.users"), XlsxCell.Number(studio.Users));
            sheet.AddRow(words.W("credits.studio.credits"), studio.Credits);
            sheet.AddRow(words.W("credits.studio.prior"), studio.PriorCredits.HasValue ? (object)studio.PriorCredits.Value : words.W("studio.notCompared"));
            sheet.AddRow(words.W("credits.studio.holders"), studio.HolderCredits);
            sheet.AddRow(words.W("studio.valued"), studio.WindowValue);
            if (summary.Sources.Studio.FailureCode != null)
                sheet.AddRow(words.W("credits.studio.failed"));
        }

        /// <summary>The page's labels for a policy's source codes (the portal's <c>POLICY_STATUS_KEYS</c> and <c>BILLING_METHOD_KEYS</c>).</summary>
        private const string PolicyStatus = "copilotInvestment.policies.status.";
        private const string BillingMethod = "copilotInvestment.policies.billing.";

        /// <summary>The services a policy has switched on - the ones the page lists.</summary>
        private const string EnabledService = "enabled";

        /// <summary>Who a policy covers, as the page words it; the number of groups is a column of its own.</summary>
        private static string PolicyScope(string type, Words words) =>
            type == "allUsers" ? words.T("copilotInvestment.policies.scope.allUsers")
            : type == "selectedGroups" ? words.T("copilotInvestment.policies.scope.selectedGroupsUnknown")
            : words.T("copilotInvestment.policies.scope.other");

        private static string BillingMethods(string methods, Words words) =>
            string.Join(" \u00b7 ", (methods ?? string.Empty).Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).Select(m => words.Label(BillingMethod, m)));

        /// <summary>
        /// A limit as the source states it: its amount and unit (source data), the product's word for "unlimited", or - as on
        /// the page - "unknown" for a limit the source does not state or states in a shape this build does not read.
        /// </summary>
        private static string QuantityText(CopilotInvestmentQuantity quantity, Words words)
        {
            if (quantity != null && quantity.Kind == CopilotInvestmentQuantityKinds.Unlimited) return words.W("credits.unlimited");
            if (quantity != null && quantity.Kind == CopilotInvestmentQuantityKinds.Limited && quantity.Value.HasValue)
                return (words.Num(quantity.Value.Value, 6) + " " + (quantity.Unit ?? string.Empty)).TrimEnd();
            return words.T("copilotInvestment.value.unknown");
        }
    }
}

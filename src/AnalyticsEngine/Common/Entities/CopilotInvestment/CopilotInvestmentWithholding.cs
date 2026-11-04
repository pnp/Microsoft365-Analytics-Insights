using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// The Copilot investment report's totals for a reader without See PII: the Copilot Studio and balance totals and their
    /// parts, the figures of the people without a seat, the adoption bands, and the modelled cost with everything computed
    /// from it. A figure is withheld when one to four people are behind it, and so is any other figure from which it could be
    /// worked out again - a total less the parts shown beside it, or a return from the value it is quoted with.
    /// </summary>
    /// <remarks>
    /// <para>The Studio credits split into the analysed seat holders' (the Studio part of the modelled cost, and the bands'
    /// Studio credits), other seat holders' and those of people without a seat (the metered figures); the totals for all
    /// seat holders and for everyone add them up. The balances split the same way, plus the quiet balances that add nobody
    /// to the report. Each split is decided by <see cref="SmallPopulations.Parts"/>.</para>
    /// <para>The analysed holders' Studio credits are never withheld only to protect another part: they are in the cost the
    /// return is measured against, which every return, net figure and break-even would then go with. When nothing else can
    /// protect a part, the Studio total is withheld instead, and one organisation group with Studio credits with it, because
    /// the groups add up to that total.</para>
    /// <para>Every object changed is a copy: the cached summary is the one a reader with See PII is given.</para>
    /// </remarks>
    internal static class CopilotInvestmentWithholding
    {
        /// <summary>Parts of <see cref="CopilotInvestmentStudioSpend.Contributors"/>.</summary>
        internal const int StudioAnalysedHolders = 0, StudioOtherHolders = 1, StudioWithoutSeat = 2;

        /// <summary>Parts of <see cref="CopilotInvestmentCreditBalances.Contributors"/>.</summary>
        internal const int BalanceAnalysedHolders = 0, BalanceOtherHolders = 1, BalanceWithoutSeat = 2, BalanceQuiet = 3;

        /// <summary>What the organisation groups and the quadrants, which other code trims, must withhold to agree with the totals.</summary>
        internal sealed class Decisions
        {
            public bool StudioPriced { get; set; }
            public bool QuadrantTotalWithheld { get; set; }
            public int[] GroupTotalsWithheld { get; set; } = new int[0];
        }

        internal static Decisions Apply(CopilotInvestmentSummary source, CopilotInvestmentSummary copy, int minPeople)
        {
            var decisions = new Decisions();
            var investment = source.Investment;
            if (investment == null) return decisions;

            var studio = new SmallPopulations.Parts(investment.Studio?.Contributors ?? new int[3], minPeople, keep: StudioAnalysedHolders);
            var balances = new SmallPopulations.Parts(investment.Credits?.Contributors ?? new int[4], minPeople);
            var studioPriced = investment.MonthlyStudioCost.HasValue;
            var showStudioTotal = studio.ShowsTotal;
            var showStudioAnalysed = studio.Shows(StudioAnalysedHolders);
            var showStudioWithoutSeat = studio.Shows(StudioWithoutSeat);
            var showBalanceAnalysed = balances.Shows(BalanceAnalysedHolders);
            var costWithheld = studioPriced && !showStudioAnalysed;
            var priorWithheld = investment.Studio?.PriorCredits != null && SmallPopulations.IsSmall(investment.Studio.PriorContributors, minPeople);

            decisions.StudioPriced = studioPriced;
            decisions.QuadrantTotalWithheld = !balances.Shows(BalanceAnalysedHolders, BalanceWithoutSeat);
            var groupTotals = new List<int>();
            if (!balances.Shows(BalanceAnalysedHolders, BalanceOtherHolders, BalanceWithoutSeat)) groupTotals.Add(CopilotInvestmentGroupRow.CreditPopulation);
            if (!showStudioTotal) groupTotals.Add(CopilotInvestmentGroupRow.StudioPopulation);
            if (costWithheld) groupTotals.Add(CopilotInvestmentGroupRow.HolderStudioPopulation);
            decisions.GroupTotalsWithheld = groupTotals.ToArray();

            var spend = investment.Copy();
            if (investment.Studio != null)
            {
                var s = investment.Studio.Copy();
                if (!showStudioTotal)
                {
                    s.CreditsWithheld = true;
                    s.Credits = null;
                    s.WindowValue = null;
                }
                if (!studio.Shows(StudioAnalysedHolders, StudioOtherHolders))
                {
                    s.HolderCreditsWithheld = true;
                    s.HolderCredits = null;
                    s.HolderWindowValue = null;
                }
                if (priorWithheld)
                {
                    s.PriorCreditsWithheld = true;
                    s.PriorCredits = null;
                }
                spend.Studio = s;
            }
            if (investment.Credits != null)
            {
                var c = investment.Credits.Copy();
                if (!balances.ShowsTotal)
                {
                    c.CreditsWithheld = true;
                    c.Credits = null;
                    c.BalanceValue = null;
                }
                if (!showBalanceAnalysed)
                {
                    c.HolderCreditsWithheld = true;
                    c.HolderCredits = null;
                }
                if (!balances.Shows(BalanceOtherHolders, BalanceWithoutSeat, BalanceQuiet))
                {
                    c.NonHolderCreditsWithheld = true;
                    c.NonHolderCredits = null;
                }
                spend.Credits = c;
            }
            if (costWithheld)
            {
                spend.StudioCostWithheld = true;
                spend.MonthlyStudioCost = null;
                spend.ModelledMonthlyCost = null;
                spend.ModelledWindowCost = null;
            }
            copy.Investment = spend;

            if (source.Metered != null)
            {
                var metered = source.Metered.Copy();
                if (SmallPopulations.IsSmall(metered.ActiveUsers, minPeople))
                {
                    metered.ActiveDaysWithheld = true;
                    metered.ActiveDays = null;
                }
                if (!showStudioWithoutSeat)
                {
                    metered.StudioWithheld = true;
                    metered.StudioCredits = null;
                    metered.StudioWindowValue = null;
                }
                if (!balances.Shows(BalanceWithoutSeat))
                {
                    metered.CreditsWithheld = true;
                    metered.Credits = null;
                    metered.CreditBalanceValue = null;
                }
                copy.Metered = metered;
            }
            if (source.Evidence != null && SmallPopulations.IsSmall(source.Evidence.UnlicensedActiveUsers, minPeople))
            {
                var evidence = source.Evidence.Copy();
                evidence.UnlicensedActiveDaysWithheld = true;
                evidence.UnlicensedActiveDays = null;
                copy.Evidence = evidence;
            }

            copy.Cohorts = CopilotInvestmentCohort.WithoutSmallPopulations(source.Cohorts, minPeople, studioPriced,
                creditsTotalWithheld: !showBalanceAnalysed, studioTotalWithheld: !showStudioAnalysed);

            var stressWithheld = costWithheld || !showBalanceAnalysed;
            if (costWithheld) WithholdCost(source, copy);
            else if (stressWithheld && source.Sensitivity?.RoiBaseIfBalancesMonthly != null)
            {
                // The what-if adds the analysed holders' balances to the cost: with the cost known, it would give them back.
                copy.Sensitivity = source.Sensitivity.Copy();
                copy.Sensitivity.RoiBaseIfBalancesMonthly = null;
            }

            copy.Actions = source.Actions?.Select(a =>
            {
                var heavy = a.Code == CopilotInvestmentActionCodes.LicenceMeteredHeavy && a.MonthlyAmount.HasValue
                    && (SmallPopulations.IsSmall(a.People, minPeople)
                        || (showStudioWithoutSeat && SmallPopulations.IsSmall((investment.Studio?.Contributors?[StudioWithoutSeat] ?? 0) - a.People, minPeople)));
                var belowBreakEven = costWithheld && a.Code == CopilotInvestmentActionCodes.EnableBelowBreakEven && a.MonthlyAmount.HasValue;
                var reclaim = costWithheld && a.RoiBaseAfter.HasValue;
                if (!heavy && !belowBreakEven && !reclaim) return a;
                var action = a.Copy();
                if (heavy || belowBreakEven)
                {
                    action.MonthlyAmount = null;
                    action.MonthlyAmountWithheld = true;
                }
                if (reclaim) action.RoiBaseAfter = null;
                return action;
            }).ToList();

            // A finding goes with its headline figure; the return's range and break-even are on the value tab, withheld there too.
            copy.Findings = source.Findings?.Where(f =>
                    !(costWithheld && (f.Code == CopilotInvestmentFindingCodes.BreakEven || f.Code == CopilotInvestmentFindingCodes.ValueRange))
                    && !(f.Code == CopilotInvestmentFindingCodes.StudioTrend && (!showStudioTotal || priorWithheld))
                    && !(f.Code == CopilotInvestmentFindingCodes.BalancesUnverified && !balances.ShowsTotal))
                .Select(f =>
                {
                    if (costWithheld && f.Code == CopilotInvestmentFindingCodes.IdleSeats) return f.Without(null, "roiAfter");
                    if (stressWithheld && f.Code == CopilotInvestmentFindingCodes.BalancesUnverified) return f.Without(null, "roiIfMonthly");
                    return f;
                }).ToList();
            return decisions;
        }

        /// <summary>
        /// Withholds the modelled cost's consequences: every return (a value over the cost), net figure (a value less it), the
        /// break-even minutes and margin (the cost over the value of a minute) and the path's gap and the days that close it.
        /// With the value beside them, any of these gives the cost back, and the cost less the seats the Studio charges.
        /// </summary>
        private static void WithholdCost(CopilotInvestmentSummary source, CopilotInvestmentSummary copy)
        {
            if (source.Value != null)
            {
                var value = source.Value.Copy();
                value.Scenarios = source.Value.Scenarios?.Select(s =>
                {
                    var scenario = s.Copy();
                    scenario.RoiMultiple = null;
                    scenario.Net = null;
                    return scenario;
                }).ToList();
                copy.Value = value;
            }
            if (source.BreakEven != null)
            {
                var breakEven = source.BreakEven.Copy();
                breakEven.MinutesPerActiveDay = null;
                breakEven.MarginOfSafety = null;
                copy.BreakEven = breakEven;
            }
            if (source.Sensitivity != null)
            {
                var sensitivity = source.Sensitivity.Copy();
                sensitivity.Grid = source.Sensitivity.Grid?.Select(g =>
                {
                    var cell = g.Copy();
                    cell.RoiMultiple = null;
                    return cell;
                }).ToList();
                sensitivity.RoiBaseHourlyMinus20 = null;
                sensitivity.RoiBaseHourlyPlus20 = null;
                sensitivity.RoiBaseIfBalancesMonthly = null;
                sensitivity.RoiBaseAfterReclaim = null;
                copy.Sensitivity = sensitivity;
            }
            if (source.Path != null)
            {
                var path = source.Path.Copy();
                path.MonthlyCost = null;
                path.MonthlyGap = null;
                path.ExtraActiveDaysPerMonth = null;
                path.ExtraDaysPerActiveHolder = null;
                path.MinutesNeeded = null;
                path.RoiAfterReclaim = null;
                copy.Path = path;
            }
        }
    }
}

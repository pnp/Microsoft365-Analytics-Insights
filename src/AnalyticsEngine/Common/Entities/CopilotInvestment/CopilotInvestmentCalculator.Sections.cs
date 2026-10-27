using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    public static partial class CopilotInvestmentCalculator
    {
        /// <summary>The share of the top of a population a concentration figure is measured over.</summary>
        public const double EvidenceTopFraction = 0.2;
        public const double CreditTopFraction = 0.1;

        /// <summary>The most groups an organisation breakdown lists; the rest are folded into one row.</summary>
        public const int MaxGroups = 100;

        public static class GroupKeys
        {
            public const string NotSet = "notSet";
            public const string OtherSmallGroups = "otherSmallGroups";
        }

        public static class QuadrantKeys
        {
            public const string HighSpendStrongEvidence = "highSpendStrongEvidence";
            public const string HighSpendThinEvidence = "highSpendThinEvidence";
            public const string LowSpendStrongEvidence = "lowSpendStrongEvidence";
            public const string LowSpendThinEvidence = "lowSpendThinEvidence";
        }

        private static List<CopilotInvestmentCohort> Cohorts(Context context)
        {
            var cohorts = new List<CopilotInvestmentCohort>();
            var holders = context.Holders;
            var totalDays = holders.Sum(h => (long)h.ActiveDays);
            foreach (var band in Enum.GetValues(typeof(AdoptionBand)).Cast<AdoptionBand>().OrderByDescending(b => b))
            {
                var members = holders.Where(h => h.Band == band.ToString()).ToList();
                var days = members.Sum(h => (long)h.ActiveDays);
                var cohort = new CopilotInvestmentCohort
                {
                    Band = band.ToString(),
                    Holders = members.Count,
                    ActiveDays = days,
                    MedianActiveDays = CopilotInvestmentStatistics.Median(members.Select(h => (double)h.ActiveDays)),
                    ShareOfHolders = holders.Count > 0 ? Math.Round(members.Count / (double)holders.Count, 4) : (double?)null,
                    ShareOfActiveDays = totalDays > 0 ? Math.Round(days / (double)totalDays, 4) : (double?)null,
                    Credits = Math.Round(members.Sum(h => h.Credit?.Credits ?? 0), 6),
                    StudioCredits = Math.Round(members.Sum(h => h.Studio?.Credits ?? 0), 6),
                };
                if (context.SeatPrice.HasValue) cohort.Cost = Money(members.Sum(h => context.Cost(h) ?? 0));
                if (context.ValueReady)
                {
                    var value = members.Sum(h => context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base));
                    cohort.ValueBase = Money(value);
                    cohort.RoiBase = Ratio(value, cohort.Cost);
                }
                cohorts.Add(cohort);
            }
            return cohorts;
        }

        private static CopilotInvestmentOrganisation Organisation(CopilotInvestmentInput input, Context context)
        {
            var organisation = new CopilotInvestmentOrganisation
            {
                Dimension = input.Dimension,
                DimensionName = input.DimensionName,
                MinGroupSize = Math.Max(1, input.MinGroupSize),
            };
            if (input.GroupOf == null) return organisation;

            var groups = (input.People ?? new List<CopilotInvestmentPerson>())
                .GroupBy(p => NullIfBlank(input.GroupOf(p.UserId)), StringComparer.Ordinal)
                .Select(g => new { Label = g.Key, People = g.ToList() })
                .ToList();

            var shown = groups.Where(g => g.People.Count >= organisation.MinGroupSize)
                .OrderByDescending(g => g.People.Count(p => p.Licensed))
                .ThenByDescending(g => g.People.Count)
                .ThenBy(g => g.Label, StringComparer.Ordinal)
                .Take(MaxGroups)
                .ToList();
            var folded = groups.Except(shown).ToList();

            foreach (var group in shown)
            {
                var row = GroupRow(context, group.People);
                if (group.Label == null) row.EmptyKey = GroupKeys.NotSet;
                else row.Label = group.Label;
                organisation.Groups.Add(row);
            }

            if (folded.Count > 0)
            {
                var row = GroupRow(context, folded.SelectMany(g => g.People).ToList());
                row.EmptyKey = GroupKeys.OtherSmallGroups;
                organisation.Groups.Add(row);
                organisation.FoldedGroups = folded.Count;
            }
            return organisation;
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static CopilotInvestmentGroupRow GroupRow(Context context, List<CopilotInvestmentPerson> people)
        {
            var holders = people.Where(p => p.Licensed).ToList();
            var active = holders.Count(h => h.ActiveDays > 0);
            var row = new CopilotInvestmentGroupRow
            {
                Holders = holders.Count,
                ActiveHolders = active,
                IdleHolders = holders.Count - active,
                ActiveShare = holders.Count > 0 ? Math.Round(active / (double)holders.Count, 4) : (double?)null,
                ActiveDaysPerHolderMonthly = holders.Count > 0 ? Math.Round(holders.Sum(h => h.ActiveDays) / (double)holders.Count / context.Months, 2) : (double?)null,
                Credits = Math.Round(people.Sum(p => p.Credit?.Credits ?? 0), 6),
                CreditConsumers = people.Count(p => (p.Credit?.Credits ?? 0) > 0),
                StudioCredits = Math.Round(people.Sum(p => p.Studio?.Credits ?? 0), 6),
                UnlicensedActiveUsers = people.Count(p => !p.Licensed && p.ActiveDays > 0),
            };
            if (context.SeatPrice.HasValue) row.Cost = Money(holders.Sum(h => context.Cost(h) ?? 0));
            if (context.ValueReady)
            {
                var value = holders.Sum(h => context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base));
                row.ValueBase = Money(value);
                row.RoiBase = Ratio(value, row.Cost);
            }
            return row;
        }

        private static CopilotInvestmentConcentration Concentration(Context context)
        {
            var result = new CopilotInvestmentConcentration
            {
                ActiveDays = CopilotInvestmentStatistics.Distribution(context.Holders.Select(h => (double)h.ActiveDays).ToList(), EvidenceTopFraction),
            };
            var consumers = (context.Input.People ?? new List<CopilotInvestmentPerson>()).Where(p => (p.Credit?.Credits ?? 0) > 0).ToList();
            result.Credits = CopilotInvestmentStatistics.Distribution(consumers.Select(p => p.Credit.Credits.Value).ToList(), CreditTopFraction);
            if (consumers.Count > 0)
            {
                var median = CopilotInvestmentStatistics.Median(consumers.Select(p => p.Credit.Credits.Value)).Value;
                foreach (var key in new[] { QuadrantKeys.HighSpendStrongEvidence, QuadrantKeys.HighSpendThinEvidence, QuadrantKeys.LowSpendStrongEvidence, QuadrantKeys.LowSpendThinEvidence })
                    result.Quadrants.Add(new CopilotInvestmentQuadrant { Key = key });
                foreach (var person in consumers)
                {
                    var high = person.Credit.Credits.Value >= median;
                    var strong = context.ActiveDaysPerMonth(person) >= StrongEvidenceDaysPerMonth / 2;
                    var key = high ? (strong ? QuadrantKeys.HighSpendStrongEvidence : QuadrantKeys.HighSpendThinEvidence)
                        : (strong ? QuadrantKeys.LowSpendStrongEvidence : QuadrantKeys.LowSpendThinEvidence);
                    var quadrant = result.Quadrants.Single(q => q.Key == key);
                    quadrant.People++;
                    quadrant.Credits = Math.Round(quadrant.Credits + person.Credit.Credits.Value, 6);
                }
            }
            return result;
        }

        private static CopilotInvestmentMetered Metered(CopilotInvestmentInput input, Context context)
        {
            var people = context.NonHolders;
            var metered = new CopilotInvestmentMetered
            {
                People = people.Count,
                ActiveUsers = people.Count(p => p.ActiveDays > 0),
                ActiveDays = people.Sum(p => (long)p.ActiveDays),
                StudioUsers = people.Count(p => (p.Studio?.Credits ?? 0) > 0),
                StudioCredits = Math.Round(people.Sum(p => p.Studio?.Credits ?? 0), 6),
                CreditUsers = people.Count(p => (p.Credit?.Credits ?? 0) > 0),
                Credits = Math.Round(people.Sum(p => p.Credit?.Credits ?? 0), 6),
                RecommendedCandidates = input.RecommendedCandidates,
            };
            if (context.StudioPrice.HasValue) metered.StudioWindowValue = Money(metered.StudioCredits * context.StudioPrice.Value);
            if (context.CreditPrice.HasValue) metered.CreditBalanceValue = Money(metered.Credits * context.CreditPrice.Value);
            if (context.SeatPrice.HasValue && context.StudioPrice.HasValue)
                metered.StudioAboveSeatPrice = people.Count(p => (p.Studio?.Credits ?? 0) * context.StudioPrice.Value / context.Months >= context.SeatPrice.Value);
            if (context.SeatPrice.HasValue && context.CreditPrice.HasValue)
                metered.BalanceAboveSeatPriceIfMonthly = people.Count(p => (p.Credit?.Credits ?? 0) * context.CreditPrice.Value >= context.SeatPrice.Value);
            if (context.BreakEvenDaysPerMonth.HasValue)
                metered.EvidencedCandidates = people.Count(p => p.ActiveDays > 0 && context.ActiveDaysPerMonth(p) >= context.BreakEvenDaysPerMonth.Value);
            return metered;
        }

        private static List<CopilotInvestmentServiceRow> Services(CopilotInvestmentInput input, Context context)
        {
            var credits = input.Credits ?? new CreditFacts();
            var objects = new HashSet<Guid>((input.People ?? new List<CopilotInvestmentPerson>()).Where(p => p.Credit != null).Select(p => p.Credit.ObjectId));
            if (!input.Filtered)
                foreach (var unresolved in credits.Users.Where(u => !u.UserId.HasValue)) objects.Add(unresolved.ObjectId);

            var rows = credits.Services.Where(s => objects.Contains(s.ObjectId))
                .GroupBy(s => new { s.Category, Unit = s.Unit ?? string.Empty })
                .Select(g => new CopilotInvestmentServiceRow
                {
                    Category = g.Key.Category,
                    Unit = g.Key.Unit.Length == 0 ? null : g.Key.Unit,
                    Consumers = g.Where(s => (s.Consumed ?? 0) > 0).Select(s => s.ObjectId).Distinct().Count(),
                    Quantity = Math.Round(g.Sum(s => s.Consumed ?? 0), 6),
                    UnknownAmountUsers = g.Where(s => !s.Consumed.HasValue).Select(s => s.ObjectId).Distinct().Count(),
                    LimitExhaustedUsers = g.Where(s => string.Equals(s.DecisionReason, "limitExhausted", StringComparison.Ordinal)).Select(s => s.ObjectId).Distinct().Count(),
                    NotEntitledUsers = g.Where(s => string.Equals(s.DecisionReason, "notEntitled", StringComparison.Ordinal)).Select(s => s.ObjectId).Distinct().Count(),
                    Policies = g.Select(s => s.PolicyId).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList(),
                })
                .ToList();

            foreach (var group in credits.Unevaluated.Where(u => objects.Contains(u.ObjectId)).GroupBy(u => u.Category, StringComparer.Ordinal))
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.Category, group.Key, StringComparison.Ordinal));
                if (row == null)
                {
                    row = new CopilotInvestmentServiceRow { Category = group.Key };
                    rows.Add(row);
                }
                row.UnevaluatedUsers = group.Select(u => u.ObjectId).Distinct().Count();
            }

            return rows.OrderByDescending(r => string.Equals(r.Unit, CopilotInvestmentSql.CreditUnit, StringComparison.Ordinal))
                .ThenByDescending(r => r.Quantity)
                .ThenBy(r => r.Category, StringComparer.Ordinal)
                .ThenBy(r => r.Unit, StringComparer.Ordinal)
                .ToList();
        }

        private static List<CopilotInvestmentPolicyRow> Policies(CopilotInvestmentInput input)
        {
            var context = input.Credits?.Context;
            if (context == null) return new List<CopilotInvestmentPolicyRow>();
            return context.Policies.Select(p => new CopilotInvestmentPolicyRow
            {
                Id = p.Id,
                DisplayName = p.DisplayName,
                Type = PolicyType(p.Type),
                Status = p.Status,
                BillingMethods = p.BillingMethods,
                PeriodLimit = Quantity(p.PeriodLimit),
                PerUserLimit = Quantity(p.PerUserLimit),
                Services = (context.PolicyServices.TryGetValue(p.Id, out var services) ? services : new List<CopilotCosts.CostPolicyServiceContext>())
                    .Select(s => new CopilotInvestmentPolicyService { ServiceId = s.ServiceId, State = s.State })
                    .OrderBy(s => s.ServiceId, StringComparer.Ordinal).ToList(),
                AssignedGroups = context.PolicyGroupCounts.TryGetValue(p.Id, out var groups) ? groups : (int?)null,
                ModifiedAt = p.ModifiedAt,
            }).OrderBy(p => p.DisplayName ?? p.Id, StringComparer.Ordinal).ToList();
        }

        private static string PolicyType(string odataType)
        {
            if (string.Equals(odataType, "#microsoft.graph.allUsersSpendingPolicy", StringComparison.Ordinal)) return "allUsers";
            if (string.Equals(odataType, "#microsoft.graph.selectedGroupsSpendingPolicy", StringComparison.Ordinal)) return "selectedGroups";
            return "other";
        }

        private static CopilotInvestmentQuantity Quantity(CopilotCosts.CostQuantity quantity) =>
            quantity == null ? null : new CopilotInvestmentQuantity
            {
                Kind = CopilotInvestmentQuantityKinds.Of(quantity.Type),
                Value = quantity.Value,
                Unit = quantity.Unit,
            };

        private static List<CopilotInvestmentTenantBalanceRow> TenantBalances(CopilotInvestmentInput input)
        {
            var balances = input.Credits?.Context?.TenantBalances;
            if (balances == null) return new List<CopilotInvestmentTenantBalanceRow>();
            return balances.Select(b => new CopilotInvestmentTenantBalanceRow
            {
                BillingMethod = b.BillingMethod,
                Purchased = b.PurchasedQuantity,
                Consumed = b.ConsumedQuantity,
                Remaining = b.RemainingQuantity,
                Unit = b.Unit,
                LastModified = b.Freshness?.LastModifiedDateTime,
            }).ToList();
        }

        private static List<CopilotInvestmentAction> Actions(CopilotInvestmentInput input, Context context, CopilotInvestmentSummary summary)
        {
            var actions = new List<CopilotInvestmentAction>();
            var holders = context.Holders;
            var cost = ModelledCost(context);
            var baseValue = context.ValueReady ? holders.Sum(h => context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base)) : 0;

            var reclaim = holders.Where(context.IsReclaimCandidate).ToList();
            if (reclaim.Count > 0)
            {
                var reclaimCost = reclaim.Sum(h => context.Cost(h) ?? 0);
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.ReclaimIdle,
                    People = reclaim.Count,
                    MonthlyAmount = context.SeatPrice.HasValue ? Money(reclaim.Count * context.SeatPrice.Value) : null,
                    RoiBaseAfter = context.ValueReady && cost.HasValue ? Ratio(baseValue, cost.Value - reclaimCost) : null,
                    Flag = CopilotInvestmentFlags.ReclaimCandidate,
                });
            }

            if (context.ValueReady)
            {
                var below = holders.Where(h => h.ActiveDays > 0
                    && context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base) < (context.Cost(h) ?? 0)).ToList();
                if (below.Count > 0)
                {
                    actions.Add(new CopilotInvestmentAction
                    {
                        Code = CopilotInvestmentActionCodes.EnableBelowBreakEven,
                        People = below.Count,
                        MonthlyAmount = Money(below.Sum(h => (context.Cost(h) ?? 0) - context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base)) / context.Months),
                        Metric = context.BreakEvenDaysPerMonth.HasValue
                            ? CopilotInvestmentStatistics.Median(below.Select(h => Math.Max(0, context.BreakEvenDaysPerMonth.Value - context.ActiveDaysPerMonth(h))))
                            : null,
                        Signal = CopilotInvestmentSignals.BelowBreakEven,
                    });
                }
            }
            else
            {
                var light = holders.Count(h => h.ActiveDays > 0 && !BandAtLeast(h.Band, AdoptionBand.Developing));
                if (light > 0)
                    actions.Add(new CopilotInvestmentAction { Code = CopilotInvestmentActionCodes.EnableLightEvidence, People = light, Signal = CopilotInvestmentSignals.LightEvidence });
            }

            if (context.BreakEvenDaysPerMonth.HasValue)
            {
                var evidenced = context.NonHolders.Count(p => p.ActiveDays > 0 && context.ActiveDaysPerMonth(p) >= context.BreakEvenDaysPerMonth.Value);
                if (evidenced > 0)
                    actions.Add(new CopilotInvestmentAction
                    {
                        Code = CopilotInvestmentActionCodes.ExpandEvidenced,
                        People = evidenced,
                        MonthlyAmount = Money(evidenced * context.SeatPrice.Value),
                        Metric = Math.Round(context.BreakEvenDaysPerMonth.Value, 2),
                        Signal = CopilotInvestmentSignals.ExpandCandidate,
                    });
            }
            else if (input.RecommendedCandidates > 0)
            {
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.ExpandRecommended,
                    People = input.RecommendedCandidates,
                    MonthlyAmount = context.SeatPrice.HasValue ? Money(input.RecommendedCandidates * context.SeatPrice.Value) : null,
                    Signal = CopilotInvestmentSignals.ExpandCandidate,
                });
            }

            var highSpend = context.Rows.Where(r => r.Flags.Contains(CopilotInvestmentFlags.HighSpendThinEvidence)).ToList();
            if (highSpend.Count > 0)
            {
                var credits = highSpend.Sum(r => r.Credits ?? 0);
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.ReviewHighSpendThinEvidence,
                    People = highSpend.Count,
                    Credits = Math.Round(credits, 6),
                    MonthlyAmount = context.CreditPrice.HasValue ? Money(credits * context.CreditPrice.Value) : null,
                    AssumesMonthlyBalances = context.CreditPrice.HasValue,
                    Flag = CopilotInvestmentFlags.HighSpendThinEvidence,
                });
            }

            var blocked = (input.People ?? new List<CopilotInvestmentPerson>())
                .Where(p => p.Credit != null && (p.Credit.LimitExhaustedRows > 0 || p.Credit.NearLimitRows > 0) && context.StrongEvidence(p)).ToList();
            if (blocked.Count > 0)
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.RaiseLimitEvidenced,
                    People = blocked.Count,
                    Credits = Math.Round(blocked.Sum(p => p.Credit.Credits ?? 0), 6),
                    Flag = CopilotInvestmentFlags.LimitExhausted,
                });

            if (context.SeatPrice.HasValue && context.StudioPrice.HasValue)
            {
                var heavy = context.NonHolders.Where(p => (p.Studio?.Credits ?? 0) * context.StudioPrice.Value / context.Months >= context.SeatPrice.Value).ToList();
                if (heavy.Count > 0)
                    actions.Add(new CopilotInvestmentAction
                    {
                        Code = CopilotInvestmentActionCodes.LicenceMeteredHeavy,
                        People = heavy.Count,
                        MonthlyAmount = Money(heavy.Sum(p => p.Studio.Credits) * context.StudioPrice.Value / context.Months),
                        Signal = CopilotInvestmentSignals.Metered,
                    });
            }

            var sources = summary.Sources.Credits;
            var coverageGap = (sources.UnresolvedObjects ?? 0) + sources.PartialUsers + sources.StaleUsers;
            if (sources.HasData && coverageGap > 0)
                actions.Add(new CopilotInvestmentAction { Code = CopilotInvestmentActionCodes.ImproveCoverage, People = coverageGap });

            if (!context.ValueReady)
                actions.Add(new CopilotInvestmentAction { Code = CopilotInvestmentActionCodes.SetAssumptions, People = 0, Metric = summary.Value.Missing.Count });

            return actions;
        }

        private static List<CopilotInvestmentFinding> Findings(CopilotInvestmentInput input, Context context, CopilotInvestmentSummary summary)
        {
            var findings = new List<CopilotInvestmentFinding>();
            var seats = summary.Investment.Seats;
            if (seats.Holders == 0)
            {
                findings.Add(Finding(CopilotInvestmentFindingCodes.NoSeats, "neutral"));
            }
            else if (seats.MonthlyRunRate.HasValue)
            {
                findings.Add(Finding(CopilotInvestmentFindingCodes.RunRate, "neutral",
                    ("monthly", seats.MonthlyRunRate), ("holders", seats.Holders), ("price", context.SeatPrice),
                    ("costPerActive", seats.CostPerActiveHolderMonthly)));
            }

            if (summary.Value.Available)
            {
                var low = summary.Value.Scenarios.Single(s => s.Scenario == CopilotInvestmentScenarios.Conservative);
                var mid = summary.Value.Scenarios.Single(s => s.Scenario == CopilotInvestmentScenarios.Base);
                var high = summary.Value.Scenarios.Single(s => s.Scenario == CopilotInvestmentScenarios.Upside);
                var tone = (low.RoiMultiple ?? 0) >= 1 ? "good" : (mid.RoiMultiple ?? 0) >= 1 ? "warning" : "critical";
                findings.Add(Finding(CopilotInvestmentFindingCodes.ValueRange, tone,
                    ("low", low.MonthlyValue), ("base", mid.MonthlyValue), ("high", high.MonthlyValue),
                    ("roiLow", low.RoiMultiple), ("roiBase", mid.RoiMultiple), ("roiHigh", high.RoiMultiple),
                    ("cost", summary.Investment.ModelledMonthlyCost)));

                var breakEven = summary.BreakEven;
                if (breakEven.MinutesPerActiveDay.HasValue)
                {
                    var margin = breakEven.MarginOfSafety ?? -1;
                    findings.Add(Finding(CopilotInvestmentFindingCodes.BreakEven, margin >= 0.25 ? "good" : margin >= 0 ? "warning" : "critical",
                        ("minutes", breakEven.MinutesPerActiveDay), ("assumed", context.Minutes.Base), ("margin", breakEven.MarginOfSafety),
                        ("daysPerMonth", breakEven.ActiveDaysPerMonthPerSeat), ("shareAtOrAbove", breakEven.ShareAtOrAbove)));
                }
            }
            else
            {
                findings.Add(Finding(CopilotInvestmentFindingCodes.AssumptionsMissing, "warning", ("missing", summary.Value.Missing.Count)));
            }

            var evidence = summary.Evidence;
            if (evidence.AnalysedHolders > 0)
            {
                var share = evidence.IdleHolders / (double)evidence.AnalysedHolders;
                var reclaim = summary.Actions.FirstOrDefault(a => a.Code == CopilotInvestmentActionCodes.ReclaimIdle);
                findings.Add(Finding(CopilotInvestmentFindingCodes.IdleSeats, share >= 0.25 ? "critical" : share >= 0.1 ? "warning" : "good",
                    ("idle", evidence.IdleHolders), ("share", Math.Round(share, 4)), ("reclaimable", reclaim?.People ?? 0),
                    ("monthly", reclaim?.MonthlyAmount), ("roiAfter", reclaim?.RoiBaseAfter)));
            }

            var concentration = summary.Concentration;
            if (concentration.ActiveDays != null && concentration.ActiveDays.Population >= 10)
                findings.Add(Finding(CopilotInvestmentFindingCodes.EvidenceConcentration, concentration.ActiveDays.TopShare >= 0.6 ? "warning" : "neutral",
                    ("topShare", concentration.ActiveDays.TopShare), ("topFraction", EvidenceTopFraction), ("gini", concentration.ActiveDays.Gini)));
            if (concentration.Credits != null && concentration.Credits.Population >= MinConsumersForSpendFlags)
                findings.Add(Finding(CopilotInvestmentFindingCodes.CreditConcentration, concentration.Credits.TopShare >= 0.5 ? "warning" : "neutral",
                    ("topShare", concentration.Credits.TopShare), ("topFraction", CreditTopFraction), ("consumers", concentration.Credits.Population)));

            var highSpend = summary.Actions.FirstOrDefault(a => a.Code == CopilotInvestmentActionCodes.ReviewHighSpendThinEvidence);
            if (highSpend != null)
                findings.Add(Finding(CopilotInvestmentFindingCodes.HighSpendThinEvidence, "warning", ("people", highSpend.People), ("credits", highSpend.Credits)));

            var expand = summary.Actions.FirstOrDefault(a => a.Code == CopilotInvestmentActionCodes.ExpandEvidenced || a.Code == CopilotInvestmentActionCodes.ExpandRecommended);
            if (expand != null)
                findings.Add(Finding(CopilotInvestmentFindingCodes.ExpandCandidates, "opportunity", ("people", expand.People), ("monthly", expand.MonthlyAmount),
                    ("evidenced", expand.Code == CopilotInvestmentActionCodes.ExpandEvidenced ? 1 : 0)));

            var limited = summary.Investment.Credits.LimitExhaustedUsers;
            if (limited > 0)
            {
                var raise = summary.Actions.FirstOrDefault(a => a.Code == CopilotInvestmentActionCodes.RaiseLimitEvidenced);
                findings.Add(Finding(CopilotInvestmentFindingCodes.LimitBlocked, "warning", ("people", limited), ("evidenced", raise?.People ?? 0)));
            }

            var studio = summary.Investment.Studio;
            if (studio.Credits > 0 || studio.PriorCredits > 0)
                findings.Add(Finding(CopilotInvestmentFindingCodes.StudioTrend, "neutral", ("credits", studio.Credits), ("prior", studio.PriorCredits),
                    ("change", studio.PriorCredits > 0 ? Math.Round(studio.Credits / studio.PriorCredits - 1, 4) : (double?)null)));

            if (summary.Investment.Credits.ObservedUsers > 0 && summary.Investment.Credits.PeriodStatus != "verified")
                findings.Add(Finding(CopilotInvestmentFindingCodes.BalancesUnverified, "neutral",
                    ("credits", summary.Investment.Credits.Credits), ("consumers", summary.Investment.Credits.ConsumingUsers),
                    ("roiIfMonthly", summary.Sensitivity.RoiBaseIfBalancesMonthly)));

            return findings;
        }

        private static CopilotInvestmentFinding Finding(string code, string tone, params (string Key, double? Value)[] values)
        {
            var finding = new CopilotInvestmentFinding { Code = code, Tone = tone };
            foreach (var value in values) finding.Values[value.Key] = value.Value;
            return finding;
        }

        private static CopilotInvestmentTrust Trust(CopilotInvestmentInput input, Context context, CopilotInvestmentSummary summary)
        {
            var trust = new CopilotInvestmentTrust();
            void Add(string code, string impact, params (string Key, double? Value)[] values)
            {
                var reason = new CopilotInvestmentTrustReason { Code = code, Impact = impact };
                foreach (var value in values) reason.Values[value.Key] = value.Value;
                trust.Reasons.Add(reason);
            }

            var adoption = summary.Sources.Adoption;
            if (!adoption.Available) Add(CopilotInvestmentTrustCodes.AdoptionUnavailable, "low");
            else if (!adoption.AuditAvailable)
                Add(CopilotInvestmentTrustCodes.AuditMissing, adoption.UsageReportAvailable ? "medium" : "low");
            if (adoption.UsageReportSignalHolders > 0)
                Add(CopilotInvestmentTrustCodes.UsageReportSignal, "medium", ("holders", adoption.UsageReportSignalHolders));
            if (adoption.FiguresIncomplete) Add(CopilotInvestmentTrustCodes.FiguresIncomplete, "low");
            if (adoption.HoldersNotAnalysed > 0) Add(CopilotInvestmentTrustCodes.HoldersNotAnalysed, "medium", ("holders", adoption.HoldersNotAnalysed));

            var assumptions = summary.Assumptions;
            if (assumptions.Status == CopilotInvestmentAssumptionStatus.Unavailable) Add(CopilotInvestmentTrustCodes.AssumptionsUnavailable, "medium");
            else if (assumptions.MissingForValue.Count > 0) Add(CopilotInvestmentTrustCodes.AssumptionsMissing, "medium", ("missing", assumptions.MissingForValue.Count));
            if (assumptions.ScenarioApplied) Add(CopilotInvestmentTrustCodes.ScenarioApplied, "medium");

            var seats = summary.Investment.Seats;
            if (seats.Basis == SeatBases.CurrentAssignments || seats.Basis == SeatBases.Mixed)
                Add(CopilotInvestmentTrustCodes.SeatsCurrentAssignments, "medium");

            var credits = summary.Sources.Credits;
            if (!credits.HasData) Add(CopilotInvestmentTrustCodes.CreditsMissing, "medium");
            else
            {
                if (summary.Investment.Credits.PeriodStatus != "verified") Add(CopilotInvestmentTrustCodes.BalancesUnverified, "medium");
                if (credits.DirectoryUsers.HasValue && credits.DirectoryUsers.Value > 0 && credits.CollectorObserved.HasValue)
                {
                    var coverage = credits.CollectorObserved.Value / (double)credits.DirectoryUsers.Value;
                    if (coverage < 0.95) Add(CopilotInvestmentTrustCodes.CreditCoverage, "medium", ("coverage", Math.Round(coverage, 4)));
                }
                if (credits.SnapshotDate.HasValue && (input.ToUtc.Date - credits.SnapshotDate.Value.Date).TotalDays > 3)
                    Add(CopilotInvestmentTrustCodes.CreditsStale, "medium", ("days", (input.ToUtc.Date - credits.SnapshotDate.Value.Date).TotalDays));
                if (credits.ObservedUsers > 0 && credits.PartialUsers / (double)credits.ObservedUsers > 0.05)
                    Add(CopilotInvestmentTrustCodes.CreditsPartial, "medium", ("share", Math.Round(credits.PartialUsers / (double)credits.ObservedUsers, 4)));
                if ((credits.UnresolvedCredits ?? 0) > 0)
                {
                    var total = summary.Investment.Credits.Credits + credits.UnresolvedCredits.Value;
                    // An unresolved balance below 5% of the total is reported but does not lower confidence.
                    Add(CopilotInvestmentTrustCodes.CreditsUnresolved, total > 0 && credits.UnresolvedCredits.Value / total > 0.05 ? "medium" : "info",
                        ("credits", credits.UnresolvedCredits), ("objects", credits.UnresolvedObjects));
                }
            }
            if (input.WindowDays <= 7) Add(CopilotInvestmentTrustCodes.ShortWindow, "medium", ("days", input.WindowDays));
            if (summary.Sources.Studio.LastImportFailed) Add(CopilotInvestmentTrustCodes.StudioImportFailed, "medium");

            trust.Level = trust.Reasons.Any(r => r.Impact == "low") ? "low" : trust.Reasons.Any(r => r.Impact == "medium") ? "medium" : "high";
            return trust;
        }
    }
}

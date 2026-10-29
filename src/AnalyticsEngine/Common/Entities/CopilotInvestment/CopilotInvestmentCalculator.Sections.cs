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
            // The matched holders only, so a group's return compares the same people's value and cost.
            var holders = people.Where(Context.Matched).ToList();
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

        /// <summary>The active days a month the spend-against-evidence quadrants split at: about once a week.</summary>
        public const double QuadrantDaysPerMonth = StrongEvidenceDaysPerMonth / 2;

        private static CopilotInvestmentConcentration Concentration(Context context)
        {
            var result = new CopilotInvestmentConcentration
            {
                ActiveDays = CopilotInvestmentStatistics.Distribution(context.Holders.Select(h => (double)h.ActiveDays).ToList(), EvidenceTopFraction),
                QuadrantDaysPerMonth = QuadrantDaysPerMonth,
            };
            var consumers = (context.Input.People ?? new List<CopilotInvestmentPerson>()).Where(p => (p.Credit?.Credits ?? 0) > 0).ToList();
            result.Credits = CopilotInvestmentStatistics.Distribution(consumers.Select(p => p.Credit.Credits.Value).ToList(), CreditTopFraction);
            // Spend against evidence needs evidence from this window, so seat holders measured elsewhere (or not at all) sit out.
            var assessed = consumers.Where(p => !p.NotAnalysed && !p.EvidenceOtherPeriod).ToList();
            if (assessed.Count > 0)
            {
                var median = CopilotInvestmentStatistics.Median(assessed.Select(p => p.Credit.Credits.Value)).Value;
                result.QuadrantMedianCredits = Math.Round(median, 2);
                foreach (var key in new[] { QuadrantKeys.HighSpendStrongEvidence, QuadrantKeys.HighSpendThinEvidence, QuadrantKeys.LowSpendStrongEvidence, QuadrantKeys.LowSpendThinEvidence })
                    result.Quadrants.Add(new CopilotInvestmentQuadrant { Key = key });
                foreach (var person in assessed)
                {
                    var high = person.Credit.Credits.Value >= median;
                    var strong = context.ActiveDaysPerMonth(person) >= QuadrantDaysPerMonth;
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
                metered.StudioAboveSeatPrice = people.Count(context.IsMeteredAboveSeat);
            if (context.SeatPrice.HasValue && context.CreditPrice.HasValue)
                metered.BalanceAboveSeatPriceIfMonthly = people.Count(p => (p.Credit?.Credits ?? 0) * context.CreditPrice.Value >= context.SeatPrice.Value);
            if (context.BreakEvenDaysPerMonth.HasValue)
                metered.EvidencedCandidates = people.Count(context.IsExpandEvidenced);
            return metered;
        }

        private static List<CopilotInvestmentServiceRow> Services(CopilotInvestmentInput input, Context context)
        {
            var credits = input.Credits ?? new CreditFacts();
            var objects = new HashSet<Guid>(ObservedCredits(input).Select(c => c.ObjectId));
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

        /// <summary>The latest balances' categories for the given people (and, unfiltered, the accounts no user matched). Shared with Copilot Spend.</summary>
        internal static List<CopilotInvestmentServiceRow> ServiceRows(CreditFacts credits, IEnumerable<CreditUserFact> observed, bool filtered) =>
            Services(new CopilotInvestmentInput { Credits = credits, ScopedCredits = observed.ToList(), Filtered = filtered }, null);

        /// <summary>The tenant-wide spending policies of the newest complete context. Shared with Copilot Spend.</summary>
        internal static List<CopilotInvestmentPolicyRow> PolicyRows(CreditFacts credits) => Policies(new CopilotInvestmentInput { Credits = credits });

        /// <summary>The tenant-wide pool balances of the newest complete context. Shared with Copilot Spend.</summary>
        internal static List<CopilotInvestmentTenantBalanceRow> TenantBalanceRows(CreditFacts credits) =>
            TenantBalances(new CopilotInvestmentInput { Credits = credits });
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

        /// <summary>
        /// The recommendations, in a fixed order by kind of decision - seats nobody uses, seats under-used, seats to add,
        /// spend to review, then data and set-up. Amounts of different kinds are never ranked against each other: each
        /// carries an <see cref="CopilotInvestmentAction.Effect"/> saying what its money is. Every action about people is
        /// counted with the same predicate that flags them, so its drill-down lists exactly the people it counts.
        /// </summary>
        private static List<CopilotInvestmentAction> Actions(CopilotInvestmentInput input, Context context, CopilotInvestmentSummary summary)
        {
            var actions = new List<CopilotInvestmentAction>();
            var holders = context.Holders;
            var people = input.People ?? new List<CopilotInvestmentPerson>();
            double? SeatsMonthly(IEnumerable<CopilotInvestmentPerson> seats) =>
                context.SeatPrice.HasValue ? Money(seats.Sum(h => context.SeatCost(h) ?? 0) / context.Months) : null;

            var reclaim = holders.Where(context.IsReclaimCandidate).ToList();
            if (reclaim.Count > 0)
            {
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.ReclaimIdle,
                    People = reclaim.Count,
                    MonthlyAmount = SeatsMonthly(reclaim),
                    RoiBaseAfter = summary.Sensitivity.RoiBaseAfterReclaim,
                    Flag = CopilotInvestmentFlags.ReclaimCandidate,
                    Effect = CopilotInvestmentActionEffects.FreesSeatCost,
                });
            }

            var review = holders.Where(context.IsIdleReview).ToList();
            if (review.Count > 0)
            {
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.ReviewIdle,
                    People = review.Count,
                    MonthlyAmount = SeatsMonthly(review),
                    Flag = CopilotInvestmentFlags.IdleReview,
                    Effect = CopilotInvestmentActionEffects.IdleSeatCost,
                });
            }

            if (context.ValueReady)
            {
                var below = holders.Where(context.IsBelowBaseBreakEven).ToList();
                if (below.Count > 0)
                {
                    actions.Add(new CopilotInvestmentAction
                    {
                        Code = CopilotInvestmentActionCodes.EnableBelowBreakEven,
                        People = below.Count,
                        MonthlyAmount = Money(below.Sum(h => (context.Cost(h) ?? 0) - context.Value(h, CopilotInvestmentScenarios.Base, CopilotInvestmentScenarios.Base)) / context.Months),
                        // Each person's own gap, against their own cost - the days that would actually close it.
                        Metric = CopilotInvestmentStatistics.Median(below.Select(h => context.RowsById[h.UserId].BreakEvenGapDaysPerMonth ?? 0)),
                        Flag = CopilotInvestmentFlags.BelowBaseBreakEven,
                        Effect = CopilotInvestmentActionEffects.ValueShortfall,
                    });
                }
            }
            else
            {
                var light = holders.Count(h => h.ActiveDays > 0 && !BandAtLeast(h.Band, AdoptionBand.Developing));
                if (light > 0)
                    actions.Add(new CopilotInvestmentAction
                    {
                        Code = CopilotInvestmentActionCodes.EnableLightEvidence, People = light, Signal = CopilotInvestmentSignals.LightEvidence,
                        Effect = CopilotInvestmentActionEffects.None,
                    });
            }

            if (context.BreakEvenDaysPerMonth.HasValue)
            {
                var evidenced = context.NonHolders.Count(context.IsExpandEvidenced);
                if (evidenced > 0)
                    actions.Add(new CopilotInvestmentAction
                    {
                        Code = CopilotInvestmentActionCodes.ExpandEvidenced,
                        People = evidenced,
                        MonthlyAmount = Money(evidenced * context.SeatPrice.Value),
                        Metric = Math.Round(context.BreakEvenDaysPerMonth.Value, 2),
                        Flag = CopilotInvestmentFlags.ExpandEvidenced,
                        Effect = CopilotInvestmentActionEffects.AddsSeatCost,
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
                    Effect = CopilotInvestmentActionEffects.AddsSeatCost,
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
                    Effect = context.CreditPrice.HasValue ? CopilotInvestmentActionEffects.BalanceIfMonthly : CopilotInvestmentActionEffects.None,
                });
            }

            var blocked = people.Where(context.IsLimitBlockedEvidenced).ToList();
            if (blocked.Count > 0)
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.RaiseLimitEvidenced,
                    People = blocked.Count,
                    Credits = Math.Round(blocked.Sum(p => p.Credit.Credits ?? 0), 6),
                    Flag = CopilotInvestmentFlags.LimitBlockedEvidenced,
                    Effect = CopilotInvestmentActionEffects.None,
                });

            var heavy = context.NonHolders.Where(context.IsMeteredAboveSeat).ToList();
            if (heavy.Count > 0)
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.LicenceMeteredHeavy,
                    People = heavy.Count,
                    MonthlyAmount = Money(heavy.Sum(p => p.Studio.Credits) * context.StudioPrice.Value / context.Months),
                    Flag = CopilotInvestmentFlags.MeteredAboveSeat,
                    Effect = CopilotInvestmentActionEffects.MeteredSpend,
                });

            var sources = summary.Sources.Credits;
            var coverageGap = (sources.UnresolvedObjects ?? 0) + sources.PartialUsers + sources.StaleUsers;
            if (sources.HasData && coverageGap > 0)
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.ImproveCoverage, People = coverageGap, Effect = CopilotInvestmentActionEffects.None,
                });

            if (!context.ValueReady)
                actions.Add(new CopilotInvestmentAction
                {
                    Code = CopilotInvestmentActionCodes.SetAssumptions, People = 0, Metric = summary.Value.Missing.Count,
                    Effect = CopilotInvestmentActionEffects.None,
                });

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
            else if (summary.Value.UnavailableReason == CopilotInvestmentValueUnavailable.NoMatchedHolders)
            {
                findings.Add(Finding(CopilotInvestmentFindingCodes.NoMatchedHolders, "warning",
                    ("otherPeriod", summary.Investment.Seats.OtherPeriodHolders), ("notAnalysed", summary.Investment.Seats.NotAnalysedHolders)));
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
                var review = summary.Actions.FirstOrDefault(a => a.Code == CopilotInvestmentActionCodes.ReviewIdle);
                findings.Add(Finding(CopilotInvestmentFindingCodes.IdleSeats, share >= 0.25 ? "critical" : share >= 0.1 ? "warning" : "good",
                    ("idle", evidence.IdleHolders), ("share", Math.Round(share, 4)), ("reclaimable", reclaim?.People ?? 0),
                    ("monthly", reclaim?.MonthlyAmount), ("roiAfter", reclaim?.RoiBaseAfter), ("review", review?.People ?? 0),
                    ("tooNew", evidence.IdleTooNewToJudge)));
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
            if (studio.Credits > 0 || (studio.PriorCredits ?? 0) > 0)
                findings.Add(Finding(CopilotInvestmentFindingCodes.StudioTrend, "neutral", ("credits", studio.Credits), ("prior", studio.PriorCredits),
                    ("change", (studio.PriorCredits ?? 0) > 0 ? Math.Round(studio.Credits / studio.PriorCredits.Value - 1, 4) : (double?)null)));

            var trajectory = summary.Trajectory;
            if (trajectory.Available)
            {
                var measured = trajectory.Weeks.Where(w => w.ActiveHolders.HasValue).ToList();
                var first = measured.First();
                var last = measured.Last();
                var change = first.ActiveHolders.Value > 0 ? Math.Round(last.ActiveHolders.Value / first.ActiveHolders.Value - 1, 4) : (double?)null;
                findings.Add(Finding(CopilotInvestmentFindingCodes.Trajectory,
                    change.HasValue && change.Value >= 0.1 ? "good" : change.HasValue && change.Value <= -0.1 ? "warning" : "neutral",
                    ("first", first.ActiveHolders), ("last", last.ActiveHolders), ("change", change), ("weeks", measured.Count),
                    ("share", last.Share)));
            }

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

        /// <summary>
        /// How far to trust the conclusions, answered per question rather than as one label: is the evidence of use
        /// complete, is the cost complete, whose assumptions drive the money, and how good are the preview balances.
        /// </summary>
        private static CopilotInvestmentTrust Trust(CopilotInvestmentInput input, Context context, CopilotInvestmentSummary summary)
        {
            var trust = new CopilotInvestmentTrust();
            void Add(string area, string code, string impact, params (string Key, double? Value)[] values)
            {
                var reason = new CopilotInvestmentTrustReason { Code = code, Impact = impact, Area = area };
                foreach (var value in values) reason.Values[value.Key] = value.Value;
                trust.Reasons.Add(reason);
            }
            const string evidenceArea = CopilotInvestmentTrustAreas.Evidence;
            const string costArea = CopilotInvestmentTrustAreas.Cost;
            const string assumptionsArea = CopilotInvestmentTrustAreas.Assumptions;
            const string balancesArea = CopilotInvestmentTrustAreas.Balances;

            var adoption = summary.Sources.Adoption;
            if (!adoption.Available) Add(evidenceArea, CopilotInvestmentTrustCodes.AdoptionUnavailable, "low");
            else if (!adoption.AuditAvailable)
                Add(evidenceArea, CopilotInvestmentTrustCodes.AuditMissing, adoption.UsageReportAvailable ? "medium" : "low");
            if (adoption.UsageReportSignalHolders > 0)
                Add(evidenceArea, CopilotInvestmentTrustCodes.UsageReportSignal, "medium", ("holders", adoption.UsageReportSignalHolders));
            if (adoption.FiguresIncomplete) Add(evidenceArea, CopilotInvestmentTrustCodes.FiguresIncomplete, "low");
            if (adoption.HoldersNotAnalysed > 0) Add(evidenceArea, CopilotInvestmentTrustCodes.HoldersNotAnalysed, "medium", ("holders", adoption.HoldersNotAnalysed));
            if (input.WindowDays <= 7) Add(evidenceArea, CopilotInvestmentTrustCodes.ShortWindow, "medium", ("days", input.WindowDays));

            var seats = summary.Investment.Seats;
            if (seats.Basis == SeatBases.CurrentAssignments || seats.Basis == SeatBases.Mixed)
                Add(costArea, CopilotInvestmentTrustCodes.SeatsCurrentAssignments, "medium");
            var studio = summary.Sources.Studio;
            if (studio.FailureCode != null)
                // With a Studio price set, the return's cost is missing a part, so the return may be overstated.
                Add(costArea, CopilotInvestmentTrustCodes.StudioUnavailable, context.StudioPrice.HasValue ? "low" : "medium");
            else if (studio.LastImportFailed) Add(costArea, CopilotInvestmentTrustCodes.StudioImportFailed, "medium");

            var assumptions = summary.Assumptions;
            if (assumptions.Status == CopilotInvestmentAssumptionStatus.Unavailable) Add(assumptionsArea, CopilotInvestmentTrustCodes.AssumptionsUnavailable, "medium");
            else if (assumptions.MissingForValue.Count > 0) Add(assumptionsArea, CopilotInvestmentTrustCodes.AssumptionsMissing, "medium", ("missing", assumptions.MissingForValue.Count));
            if (assumptions.ScenarioApplied) Add(assumptionsArea, CopilotInvestmentTrustCodes.ScenarioApplied, "medium");
            // Always said once value is modelled: it is time back valued at a cost, not a measured saving.
            if (summary.Value.Available) Add(assumptionsArea, CopilotInvestmentTrustCodes.AssumedTimeValue, "info");

            var credits = summary.Sources.Credits;
            if (credits.FailureCode != null) Add(balancesArea, CopilotInvestmentTrustCodes.CreditsUnavailable, "medium");
            else if (!credits.HasData) Add(balancesArea, CopilotInvestmentTrustCodes.CreditsMissing, "medium");
            else
            {
                if (summary.Investment.Credits.PeriodStatus != "verified") Add(balancesArea, CopilotInvestmentTrustCodes.BalancesUnverified, "medium");
                if (credits.DirectoryUsers.HasValue && credits.DirectoryUsers.Value > 0 && credits.CollectorObserved.HasValue)
                {
                    var coverage = credits.CollectorObserved.Value / (double)credits.DirectoryUsers.Value;
                    if (coverage < 0.95) Add(balancesArea, CopilotInvestmentTrustCodes.CreditCoverage, "medium", ("coverage", Math.Round(coverage, 4)));
                }
                if (credits.SnapshotDate.HasValue && (input.ToUtc.Date - credits.SnapshotDate.Value.Date).TotalDays > 3)
                    Add(balancesArea, CopilotInvestmentTrustCodes.CreditsStale, "medium", ("days", (input.ToUtc.Date - credits.SnapshotDate.Value.Date).TotalDays));
                if (credits.ObservedUsers > 0 && credits.PartialUsers / (double)credits.ObservedUsers > 0.05)
                    Add(balancesArea, CopilotInvestmentTrustCodes.CreditsPartial, "medium", ("share", Math.Round(credits.PartialUsers / (double)credits.ObservedUsers, 4)));
                if ((credits.UnresolvedCredits ?? 0) > 0)
                {
                    var total = summary.Investment.Credits.Credits + credits.UnresolvedCredits.Value;
                    // An unresolved balance below 5% of the total is reported but does not lower confidence.
                    Add(balancesArea, CopilotInvestmentTrustCodes.CreditsUnresolved, total > 0 && credits.UnresolvedCredits.Value / total > 0.05 ? "medium" : "info",
                        ("credits", credits.UnresolvedCredits), ("objects", credits.UnresolvedObjects));
                }
            }

            string LevelOf(IEnumerable<CopilotInvestmentTrustReason> reasons)
            {
                var list = reasons.ToList();
                return list.Any(r => r.Impact == "low") ? "low" : list.Any(r => r.Impact == "medium") ? "medium" : "high";
            }
            foreach (var area in CopilotInvestmentTrustAreas.All)
                trust.Areas.Add(new CopilotInvestmentTrustArea { Area = area, Level = LevelOf(trust.Reasons.Where(r => r.Area == area)) });
            // The overall label is about the return, which the preview balances stay out of.
            trust.Level = LevelOf(trust.Reasons.Where(r => r.Area != balancesArea));
            return trust;
        }
    }
}

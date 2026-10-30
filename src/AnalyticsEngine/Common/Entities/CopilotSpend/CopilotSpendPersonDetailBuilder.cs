using Common.Entities.Copilot;
using Common.Entities.CopilotInvestment;
using Common.Entities.PromptCategories;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotSpend
{
    /// <summary>Everything one person's Copilot Spend drill-down is built from.</summary>
    public sealed class CopilotSpendPersonDetailInput
    {
        public CopilotSpendPersonRow Person { get; set; }

        /// <summary>Every person in the reader's view: the person is ranked among them.</summary>
        public IReadOnlyCollection<CopilotSpendPersonRow> People { get; set; }

        public bool StudioAvailable { get; set; }
        public bool BalancesAvailable { get; set; }

        /// <summary>The person's balances, Studio environments, turns and prompt counts.</summary>
        public PersonDetailFacts Facts { get; set; }

        /// <summary>The person's Studio days, prompts, goals, phrases and resources; null when they could not be read.</summary>
        public CopilotSpendPersonFacts Usage { get; set; }

        /// <summary>The stored agents the person's turns name.</summary>
        public IReadOnlyCollection<CopilotSpendAgentFact> Agents { get; set; }

        public DateTime FromUtc { get; set; }
        public DateTime ToExclusiveUtc { get; set; }

        /// <summary>The window's last calendar day: the person's days run from <see cref="FromUtc"/> to it, inclusive.</summary>
        public DateTime LastDay { get; set; }

        /// <summary>The Studio ledger's first and latest stored days, which say whether a day with no charges is a zero or unknown.</summary>
        public DateTime? StudioEarliest { get; set; }
        public DateTime? StudioLatest { get; set; }

        public string UsageFailureCode { get; set; }
    }

    /// <summary>
    /// Turns one person's own facts into their Copilot Spend drill-down: where they stand, where their credits went and what
    /// they used Copilot for. Descriptive only: no value, no verdict, and never the text of a prompt.
    /// </summary>
    public static class CopilotSpendPersonDetailBuilder
    {
        /// <summary>The goal the classifier gives a prompt it could not place. Counted on its own, never listed as a goal.</summary>
        public const string NotClassifiedCategoryId = "not-classified";

        public static CopilotSpendPersonDetail Build(CopilotSpendPersonDetailInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Person == null) throw new ArgumentException("A person is required.", nameof(input));
            var facts = input.Facts ?? new PersonDetailFacts();
            var person = input.Person;
            var people = input.People ?? (IReadOnlyCollection<CopilotSpendPersonRow>)new List<CopilotSpendPersonRow>();
            var detail = new CopilotSpendPersonDetail
            {
                Person = person,
                Ranks = new CopilotSpendPersonRanks(),
                Services = CopilotInvestmentPersonDetailBuilder.CreditServices(facts),
                Balances = facts.Balances.OrderBy(b => b.Date).ToList(),
                StudioEnvironments = CopilotInvestmentPersonDetailBuilder.StudioEnvironments(facts),
                UsageFailureCode = input.UsageFailureCode,
            };

            var studio = Rank(input.StudioAvailable, person.StudioCredits, people.Select(p => p.StudioCredits));
            detail.Ranks.StudioRank = studio.Rank;
            detail.Ranks.StudioConsumers = studio.Consumers;
            detail.Ranks.StudioShare = studio.Share;
            var balance = Rank(input.BalancesAvailable, person.BalanceCredits, people.Select(p => p.BalanceCredits));
            detail.Ranks.BalanceRank = balance.Rank;
            detail.Ranks.BalanceConsumers = balance.Consumers;
            detail.Ranks.BalanceShare = balance.Share;

            var usage = input.Usage;
            if (input.StudioAvailable && person.StudioCredits.HasValue && usage != null && usage.StudioAssessed)
                detail.StudioDays = CopilotSpendCalculator.CalendarDays(input.FromUtc, input.LastDay, input.StudioEarliest, input.StudioLatest, usage.StudioDays);
            detail.Usage = Usage(input, facts, usage);
            return detail;
        }

        private struct Standing
        {
            public int? Rank;
            public int? Consumers;
            public double? Share;
        }

        /// <summary>
        /// 1 for the largest amount, and people with the same amount share a rank. Of the people in view only, so a share is
        /// of the credits matched to them. Nothing for a source that is not available or a person who consumed none of it.
        /// </summary>
        private static Standing Rank(bool available, double? value, IEnumerable<double?> everyone)
        {
            if (!available) return new Standing();
            var consumed = everyone.Where(v => v > 0).Select(v => v.Value).ToList();
            var standing = new Standing { Consumers = consumed.Count };
            if (!(value > 0)) return standing;
            standing.Rank = 1 + consumed.Count(v => v > value.Value);
            var total = consumed.Sum();
            standing.Share = total > 0 ? Math.Round(value.Value / total, 4) : (double?)null;
            return standing;
        }

        private static CopilotSpendPersonUsage Usage(CopilotSpendPersonDetailInput input, PersonDetailFacts facts, CopilotSpendPersonFacts usage)
        {
            var result = new CopilotSpendPersonUsage { TurnsAssessed = facts.ActivityAssessed, PromptsAssessed = facts.PromptsAssessed };
            var from = input.FromUtc.Date;
            var activity = facts.Activity.Where(a => a.ActivityDate >= from && a.ActivityDate < input.ToExclusiveUtc).ToList();
            bool IsCowork(PersonActivityRow row) =>
                string.Equals(row.AppHost, "cowork", StringComparison.OrdinalIgnoreCase)
                || (row.AgentId.HasValue && facts.CoworkAgentIds.Contains(row.AgentId.Value));
            bool IsMakerTest(PersonActivityRow row) => !IsCowork(row) && string.Equals(row.AppHost, CopilotTurnSql.MakerTestAppHost, StringComparison.OrdinalIgnoreCase);
            bool IsNoAgent(PersonActivityRow row) => !IsCowork(row) && !IsMakerTest(row) && !row.AgentId.HasValue;

            if (facts.ActivityAssessed)
            {
                result.Turns = activity.Sum(a => a.Turns);
                result.CoworkTurns = activity.Where(IsCowork).Sum(a => a.Turns);
                result.MakerTestTurns = activity.Where(IsMakerTest).Sum(a => a.Turns);
                result.NoAgentTurns = activity.Where(IsNoAgent).Sum(a => a.Turns);
                result.AgentTurns = result.Turns - result.CoworkTurns - result.MakerTestTurns - result.NoAgentTurns;
                result.ActiveDays = activity.Where(a => a.Turns > 0).Select(a => a.ActivityDate.Date).Distinct().Count();
                result.EstimateCredits = activity.Any(a => a.EstimateCredits.HasValue) ? Math.Round(activity.Sum(a => a.EstimateCredits ?? 0), 6) : (double?)null;
                result.PricedTurns = activity.Sum(a => a.PricedTurns);

                var byDay = activity.GroupBy(a => a.ActivityDate.Date).ToDictionary(g => g.Key, g => g.ToList());
                for (var day = from; day <= input.LastDay.Date; day = day.AddDays(1))
                {
                    var rows = byDay.TryGetValue(day, out var found) ? found : new List<PersonActivityRow>();
                    result.Days.Add(new CopilotSpendUsageDay
                    {
                        Date = DateTime.SpecifyKind(day, DateTimeKind.Utc),
                        Turns = rows.Sum(r => r.Turns),
                        AgentTurns = rows.Where(r => !IsNoAgent(r)).Sum(r => r.Turns),
                    });
                }

                result.Apps = activity.Where(a => !string.IsNullOrWhiteSpace(a.AppHost))
                    .GroupBy(a => a.AppHost.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new CopilotSpendAppUse
                    {
                        App = g.Select(a => a.AppHost.Trim()).OrderBy(a => a, StringComparer.Ordinal).First(),
                        Count = g.Sum(a => a.Turns),
                        Days = g.Select(a => a.ActivityDate.Date).Distinct().Count(),
                    })
                    .OrderByDescending(a => a.Count).ThenBy(a => a.App, StringComparer.Ordinal).ToList();

                var agents = (input.Agents ?? (IReadOnlyCollection<CopilotSpendAgentFact>)new List<CopilotSpendAgentFact>())
                    .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
                result.Agents = activity.Where(a => a.AgentId.HasValue)
                    .GroupBy(a => a.AgentId.Value)
                    .Select(g =>
                    {
                        agents.TryGetValue(g.Key, out var agent);
                        var priced = g.Where(a => a.EstimateCredits.HasValue).ToList();
                        return new CopilotSpendPersonAgent
                        {
                            Id = g.Key,
                            AgentId = agent?.AgentId,
                            Name = agent?.Name,
                            KindKey = agent?.AgentId == null ? CopilotSpendAgentKindKeys.Unknown
                                : CopilotSpendAgentKindKeys.For(CopilotAgentClassifier.ClassifyStoredAgentId(agent.AgentId).Kind),
                            OriginKey = agent?.AgentId == null ? CopilotAgentOriginKeys.Unknown
                                : CopilotAgentOriginKeys.For(CopilotAgentClassifier.ResolveStoredOrigin(agent.AgentId, agent.IsCustomAgent)),
                            Cowork = g.Any(IsCowork),
                            Turns = g.Sum(a => a.Turns),
                            TestTurns = g.Where(IsMakerTest).Sum(a => a.Turns),
                            EstimateCredits = priced.Count > 0 ? Math.Round(priced.Sum(a => a.EstimateCredits.Value), 6) : (double?)null,
                            PricedTurns = g.Sum(a => a.PricedTurns),
                            Days = g.Select(a => a.ActivityDate.Date).Distinct().Count(),
                            LastDate = DateTime.SpecifyKind(g.Max(a => a.ActivityDate.Date), DateTimeKind.Utc),
                        };
                    })
                    .OrderBy(a => a.EstimateCredits.HasValue ? 0 : 1)
                    .ThenByDescending(a => a.EstimateCredits ?? 0)
                    .ThenByDescending(a => a.Turns)
                    .ThenBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(a => a.Id)
                    .ToList();
            }

            if (facts.PromptsAssessed)
            {
                result.PromptApps = facts.Prompts
                    .Where(p => string.Equals(p.InteractionType, CopilotSpendSql.PromptInteractionType, StringComparison.Ordinal))
                    .GroupBy(p => p.AppClass ?? string.Empty, StringComparer.Ordinal)
                    .Select(g => new CopilotSpendAppUse { App = g.Key, Count = g.Sum(p => p.Interactions), Days = g.Max(p => p.Days) })
                    .OrderByDescending(a => a.Count).ThenBy(a => a.App, StringComparer.Ordinal).ToList();
            }
            if (usage == null) return result;

            if (usage.PromptsAssessed)
            {
                result.Prompts = usage.PromptTotals ?? new CopilotSpendPromptTotals();
                result.Recent = usage.Recent.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.InteractionId).ToList();
                foreach (var prompt in result.Recent)
                {
                    var definition = Definition(usage.Taxonomies, prompt.TaxonomyVersion, prompt.CategoryId);
                    prompt.CategoryName = definition?.Name;
                    prompt.CategoryNameKey = definition?.NameKey;
                }
            }

            result.CategoriesAssessed = usage.CategoriesAssessed;
            result.NotClassifiedPrompts = usage.Categories
                .Where(c => string.Equals(c.CategoryId, NotClassifiedCategoryId, StringComparison.Ordinal)).Sum(c => c.Prompts);
            result.Categories = usage.Categories
                .Where(c => !string.IsNullOrEmpty(c.CategoryId) && !string.Equals(c.CategoryId, NotClassifiedCategoryId, StringComparison.Ordinal))
                .GroupBy(c => c.CategoryId, StringComparer.Ordinal)
                .Select(g =>
                {
                    // A goal kept across taxonomy versions is one row, named as the version that classified most of it names it.
                    var named = g.OrderByDescending(c => c.Prompts).ThenByDescending(c => c.TaxonomyVersion, StringComparer.Ordinal).First();
                    var definition = Definition(usage.Taxonomies, named.TaxonomyVersion, g.Key);
                    return new CopilotSpendPersonCategory
                    {
                        CategoryId = g.Key,
                        Name = definition?.Name,
                        NameKey = definition?.NameKey,
                        HumanMode = named.HumanMode ?? definition?.HumanMode,
                        Prompts = g.Sum(c => c.Prompts),
                        LastUtc = g.Max(c => c.LastUtc),
                    };
                })
                .OrderByDescending(c => c.Prompts).ThenBy(c => c.CategoryId, StringComparer.Ordinal).ToList();

            result.PhrasesAssessed = usage.PhrasesAssessed;
            result.Phrases = usage.Phrases.OrderByDescending(p => p.Prompts).ThenBy(p => p.Phrase, StringComparer.OrdinalIgnoreCase).ToList();
            result.ResourcesAssessed = usage.ResourcesAssessed;
            result.ResourceTypes = usage.ResourceTypes.OrderByDescending(r => r.Value).ThenBy(r => r.Label, StringComparer.Ordinal).ToList();
            return result;
        }

        private static PromptCategoryDefinition Definition(IReadOnlyDictionary<string, List<PromptCategoryDefinition>> taxonomies, string version, string id)
        {
            if (taxonomies == null || string.IsNullOrEmpty(version) || string.IsNullOrEmpty(id) || !taxonomies.TryGetValue(version, out var definitions))
                return null;
            return definitions?.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        }
    }
}

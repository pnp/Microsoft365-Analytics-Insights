using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>Turns one person's raw evidence rows into the drill-down: what they use Copilot for and how regularly.</summary>
    public static class CopilotInvestmentPersonDetailBuilder
    {
        public static class EvidenceCodes
        {
            public const string NoActivity = "noActivity";
            public const string RegularHabit = "regularHabit";
            public const string Occasional = "occasional";
            public const string Rare = "rare";
            public const string MultiApp = "multiApp";
            public const string AgentUse = "agentUse";
            public const string CoworkUse = "coworkUse";
            public const string Growing = "growing";
            public const string Declining = "declining";
            public const string PromptHistory = "promptHistory";
            public const string LimitReached = "limitReached";
            public const string StudioUsage = "studioUsage";

            public static readonly string[] All =
            {
                NoActivity, RegularHabit, Occasional, Rare, MultiApp, AgentUse, CoworkUse, Growing, Declining,
                PromptHistory, LimitReached, StudioUsage,
            };
        }

        /// <summary>The Monday on or before a date: weeks are counted the way the adoption report counts them.</summary>
        public static DateTime WeekStart(DateTime date)
        {
            var day = date.Date;
            var offset = ((int)day.DayOfWeek + 6) % 7;
            return DateTime.SpecifyKind(day.AddDays(-offset), DateTimeKind.Utc);
        }

        public static CopilotInvestmentPersonDetail Build(CopilotInvestmentPersonRow person, PersonDetailFacts facts, string currency,
            DateTime fromUtc, DateTime toExclusiveUtc)
        {
            facts = facts ?? new PersonDetailFacts();
            var detail = new CopilotInvestmentPersonDetail
            {
                Person = person,
                Currency = currency,
                ActivityAssessed = facts.ActivityAssessed,
                PromptsAssessed = facts.PromptsAssessed,
                Balances = facts.Balances.OrderBy(b => b.Date).ToList(),
            };

            var activity = facts.Activity.Where(a => a.ActivityDate >= fromUtc.Date && a.ActivityDate < toExclusiveUtc).ToList();
            bool IsCowork(PersonActivityRow row) =>
                string.Equals(row.AppHost, "cowork", StringComparison.OrdinalIgnoreCase)
                || (row.AgentId.HasValue && facts.CoworkAgentIds.Contains(row.AgentId.Value));

            detail.TotalTurns = activity.Sum(a => a.Turns);
            detail.CoworkTurns = activity.Where(IsCowork).Sum(a => a.Turns);
            detail.AgentTurns = activity.Where(a => a.AgentId.HasValue && !IsCowork(a)).Sum(a => a.Turns);
            detail.Apps = activity.Where(a => !string.IsNullOrWhiteSpace(a.AppHost))
                .GroupBy(a => a.AppHost, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CopilotInvestmentAppUse { App = g.First().AppHost, Turns = g.Sum(a => a.Turns), Days = g.Select(a => a.ActivityDate).Distinct().Count() })
                .OrderByDescending(a => a.Turns).ThenBy(a => a.App, StringComparer.Ordinal).ToList();

            var byDay = activity.GroupBy(a => a.ActivityDate.Date).ToDictionary(g => g.Key, g => g.Sum(a => a.Turns));
            for (var week = WeekStart(fromUtc); week < toExclusiveUtc; week = week.AddDays(7))
            {
                var days = byDay.Where(d => d.Key >= week && d.Key < week.AddDays(7)).ToList();
                detail.Weeks.Add(new CopilotInvestmentWeek { WeekStart = week, ActiveDays = days.Count, Turns = days.Sum(d => d.Value) });
            }
            detail.ActiveWeeks = detail.Weeks.Count(w => w.ActiveDays > 0);

            detail.Prompts = facts.Prompts.Where(p => string.Equals(p.InteractionType, "userPrompt", StringComparison.Ordinal)).Sum(p => p.Interactions);
            detail.Responses = facts.Prompts.Where(p => string.Equals(p.InteractionType, "aiResponse", StringComparison.Ordinal)).Sum(p => p.Interactions);
            detail.PromptApps = facts.Prompts.Where(p => string.Equals(p.InteractionType, "userPrompt", StringComparison.Ordinal))
                .GroupBy(p => p.AppClass ?? string.Empty, StringComparer.Ordinal)
                .Select(g => new CopilotInvestmentAppUse { App = g.Key, Turns = g.Sum(p => p.Interactions), Days = g.Max(p => p.Days) })
                .OrderByDescending(a => a.Turns).ToList();
            detail.PromptCategories = facts.PromptCategories
                .Select(c => new CopilotInvestmentCategoryUse { CategoryId = c.CategoryId, HumanMode = c.HumanMode, Prompts = c.Prompts })
                .OrderByDescending(c => c.Prompts).ThenBy(c => c.CategoryId, StringComparer.Ordinal).ToList();

            detail.CreditServices = CreditServices(facts);
            detail.StudioEnvironments = StudioEnvironments(facts);

            detail.Evidence = EvidenceFor(detail, person, byDay.Keys, fromUtc, toExclusiveUtc);
            return detail;
        }

        /// <summary>
        /// Every category of the person's latest balance, as the source reported it, and the categories it could not evaluate.
        /// Shared with the Copilot Spend drill-down.
        /// </summary>
        public static List<CopilotInvestmentPersonService> CreditServices(PersonDetailFacts facts) =>
            (facts ?? new PersonDetailFacts()).CreditServices.Select(s => new CopilotInvestmentPersonService
            {
                Category = s.Category,
                PolicyId = s.PolicyId,
                Consumed = s.Consumed,
                Unit = s.Unit,
                Allowed = s.Allowed,
                DecisionReason = s.DecisionReason,
                PerUserLimit = s.PerUserLimit,
                PerUserLimitKind = CopilotInvestmentQuantityKinds.Of(s.PerUserLimitType),
                Remaining = s.Remaining,
                LastModified = s.SourceModified,
            }).Concat(facts.CreditUnevaluated.Where(u => !facts.CreditServices.Any(s => string.Equals(s.Category, u.Category, StringComparison.Ordinal)))
                .Select(u => new CopilotInvestmentPersonService { Category = u.Category, UnevaluatedReason = u.Reason }))
                .OrderBy(s => s.Category, StringComparer.Ordinal).ToList();

        /// <summary>
        /// The person's billed Copilot Studio credits by Power Platform environment, largest first. Shared with the Copilot
        /// Spend drill-down. Environment, not agent: Microsoft's per-user consumption does not name the agent.
        /// </summary>
        public static List<CopilotInvestmentStudioEnvironment> StudioEnvironments(PersonDetailFacts facts) =>
            (facts ?? new PersonDetailFacts()).StudioEnvironments.Select(e => new CopilotInvestmentStudioEnvironment
            {
                EnvironmentId = e.EnvironmentId, EnvironmentName = e.EnvironmentName, Credits = Math.Round(e.Credits, 6), ActiveDays = e.ActiveDays,
            }).OrderByDescending(e => e.Credits).ThenBy(e => e.EnvironmentId, StringComparer.Ordinal).ToList();

        /// <summary>Halves shorter than this cannot show a direction of travel.</summary>
        private const int MinTrendHalfDays = 14;

        private static List<string> EvidenceFor(CopilotInvestmentPersonDetail detail, CopilotInvestmentPersonRow person,
            IEnumerable<DateTime> activeDates, DateTime fromUtc, DateTime toExclusiveUtc)
        {
            var codes = new List<string>();
            if (detail.ActivityAssessed)
            {
                var weeks = Math.Max(1, detail.Weeks.Count);
                var share = detail.ActiveWeeks / (double)weeks;
                if (detail.TotalTurns == 0) codes.Add(EvidenceCodes.NoActivity);
                else if (share >= 0.75 && detail.Weeks.Count >= 2) codes.Add(EvidenceCodes.RegularHabit);
                else if (share >= 0.25) codes.Add(EvidenceCodes.Occasional);
                else codes.Add(EvidenceCodes.Rare);
                if (detail.Apps.Count >= 3) codes.Add(EvidenceCodes.MultiApp);
                if (detail.AgentTurns > 0) codes.Add(EvidenceCodes.AgentUse);
                if (detail.CoworkTurns > 0) codes.Add(EvidenceCodes.CoworkUse);

                // Two halves of exactly equal length, compared by active days rather than prompts: week buckets that
                // straddle the window's edges would compare different spans and invent a trend from constant use.
                // In whole calendar days: a rolling window ends at "now", partway through today, and today is part of
                // it. Cutting at today's midnight made the default 28 days read as 27 - half of 13, under the minimum -
                // so no trend was ever shown for it, and left today out of the second half of a longer window.
                var start = fromUtc.Date;
                var end = toExclusiveUtc.TimeOfDay == TimeSpan.Zero ? toExclusiveUtc.Date : toExclusiveUtc.Date.AddDays(1);
                var days = (int)(end - start).TotalDays;
                var half = days / 2;
                if (half >= MinTrendHalfDays)
                {
                    var dates = activeDates.Select(d => d.Date).Distinct().ToList();
                    var first = dates.Count(d => d >= start && d < start.AddDays(half));
                    var second = dates.Count(d => d >= end.AddDays(-half) && d < end);
                    if (second >= 3 && second >= first * 1.5 && second - first >= 2) codes.Add(EvidenceCodes.Growing);
                    else if (first >= 3 && second <= first * 0.5) codes.Add(EvidenceCodes.Declining);
                }
            }
            if (detail.Prompts > 0) codes.Add(EvidenceCodes.PromptHistory);
            if (person?.Flags?.Contains(CopilotInvestmentFlags.LimitExhausted) == true) codes.Add(EvidenceCodes.LimitReached);
            if (detail.StudioEnvironments.Any(e => e.Credits > 0)) codes.Add(EvidenceCodes.StudioUsage);
            return codes;
        }
    }
}

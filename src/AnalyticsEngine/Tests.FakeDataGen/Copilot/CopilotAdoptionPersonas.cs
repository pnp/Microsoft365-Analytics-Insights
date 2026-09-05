using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// How mature a department's Copilot adoption is. Used to skew the persona mix so departments
    /// differ from each other - a generated tenant where every department scores the same makes the
    /// department treemap, the intensity scatter and the "where to target enablement" panels all
    /// look broken, because they exist precisely to show variation.
    /// </summary>
    internal enum DepartmentMaturity
    {
        /// <summary>Mostly Champions and Established users, a few stragglers.</summary>
        Leading = 0,

        /// <summary>A realistic middle: a broad hump around Developing/Established.</summary>
        Progressing = 1,

        /// <summary>Mostly unused seats - the department an enablement programme would target.</summary>
        Lagging = 2,
    }

    /// <summary>
    /// One archetype of Copilot user, expressed in exactly the three signals the adoption analysis
    /// measures: how many distinct days they were active inside the reporting window, how many
    /// interactions they had on each of those days, and how many distinct app surfaces they used.
    /// </summary>
    /// <remarks>
    /// The engagement score is
    /// <c>100 * (0.5*min(1, activeDays/12) + 0.3*min(1, perDay/5) + 0.2*min(1, apps/3))</c>
    /// using the shipped defaults (28-day window, 5 working days a week, a 0.6 frequency target -
    /// so 12 target active days - a depth target of 5 interactions per active day and a breadth
    /// target of 3 apps). Every persona below records the score that arithmetic produces, so if a
    /// tuning default changes, the expected band here is checkable rather than folklore.
    /// </remarks>
    internal sealed class AdoptionPersona
    {
        public AdoptionPersona(
            string name,
            AdoptionBand expectedBand,
            double expectedScore,
            int activeDaysInWindow,
            double interactionsPerActiveDay,
            int distinctApps,
            int priorInteractions = 0,
            int priorDaysAgo = 0,
            bool accountEnabled = true,
            bool usesAgents = false)
        {
            Name = name;
            ExpectedBand = expectedBand;
            ExpectedScore = expectedScore;
            ActiveDaysInWindow = activeDaysInWindow;
            InteractionsPerActiveDay = interactionsPerActiveDay;
            DistinctApps = distinctApps;
            PriorInteractions = priorInteractions;
            PriorDaysAgo = priorDaysAgo;
            AccountEnabled = accountEnabled;
            UsesAgents = usesAgents;
        }

        public string Name { get; }

        /// <summary>The band this persona is built to land in, for the generator's own verification.</summary>
        public AdoptionBand ExpectedBand { get; }

        /// <summary>The score the documented formula gives for this persona's signals.</summary>
        public double ExpectedScore { get; }

        public int ActiveDaysInWindow { get; }

        public double InteractionsPerActiveDay { get; }

        public int DistinctApps { get; }

        /// <summary>Interactions placed <i>before</i> the reporting window - what separates Dormant from Never used.</summary>
        public int PriorInteractions { get; }

        /// <summary>Roughly how long ago those earlier interactions were, in days.</summary>
        public int PriorDaysAgo { get; }

        public bool AccountEnabled { get; }

        /// <summary>Whether this persona's interactions should be attributed to agents.</summary>
        public bool UsesAgents { get; }

        /// <summary>Total interactions this persona generates inside the window.</summary>
        public int WindowInteractions =>
            ActiveDaysInWindow <= 0 ? 0 : Math.Max(ActiveDaysInWindow, (int)Math.Round(ActiveDaysInWindow * InteractionsPerActiveDay));
    }

    /// <summary>
    /// The catalogue of user archetypes the scenario generator plants, covering every stage of the
    /// adoption funnel and - just as importantly - several distinctly different <i>shapes</i> at a
    /// similar overall score.
    ///
    /// The shape personas are the point of the middle of this list. "Frequent but shallow", "deep but
    /// narrow" and "broad but occasional" all score in the fifties and sixties, so a tool that only
    /// reported a single number would call them the same user. They need opposite interventions, and
    /// the radar/profile visuals exist to tell them apart - which can only be demonstrated on data
    /// that actually contains all three.
    /// </summary>
    internal static class CopilotAdoptionPersonas
    {
        // --- Champions ------------------------------------------------------------------------
        // 18 days, 8/day, 5 apps -> every component capped at 1.0 -> 100.0
        public static readonly AdoptionPersona ChampionAllRound =
            new AdoptionPersona("Champion - all-round", AdoptionBand.Champion, 100.0, 18, 8, 5, usesAgents: true);

        // 9 days (0.750), 4/day (0.800), 3 apps (1.000) -> 37.5 + 24.0 + 20.0 = 81.5
        public static readonly AdoptionPersona ChampionEmerging =
            new AdoptionPersona("Champion - emerging", AdoptionBand.Champion, 81.5, 9, 4, 3, usesAgents: true);

        // --- Established ----------------------------------------------------------------------
        // 8 days (0.667), 3/day (0.600), 2 apps (0.667) -> 33.3 + 18.0 + 13.3 = 64.7
        public static readonly AdoptionPersona EstablishedBalanced =
            new AdoptionPersona("Established - balanced", AdoptionBand.Established, 64.7, 8, 3, 2, usesAgents: true);

        // 14 days (capped 1.000), 1/day (0.200), 1 app (0.333) -> 50.0 + 6.0 + 6.7 = 62.7
        // Opens Copilot most days but barely uses it, and only ever in one place.
        public static readonly AdoptionPersona EstablishedFrequentShallow =
            new AdoptionPersona("Established - frequent but shallow", AdoptionBand.Established, 62.7, 14, 1, 1);

        // 5 days (0.417), 12/day (capped 1.000), 1 app (0.333) -> 20.8 + 30.0 + 6.7 = 57.5
        // Long, heavy sessions, but rarely, and only in one surface.
        public static readonly AdoptionPersona EstablishedDeepNarrow =
            new AdoptionPersona("Established - deep but narrow", AdoptionBand.Established, 57.5, 5, 12, 1);

        // --- Developing -----------------------------------------------------------------------
        // 4 days (0.333), 2/day (0.400), 5 apps (capped 1.000) -> 16.7 + 12.0 + 20.0 = 48.7
        // Has it everywhere, uses it for very little.
        public static readonly AdoptionPersona DevelopingBroadOccasional =
            new AdoptionPersona("Developing - broad but occasional", AdoptionBand.Developing, 48.7, 4, 2, 5);

        // 5 days (0.417), 2/day (0.400), 1 app (0.333) -> 20.8 + 12.0 + 6.7 = 39.5
        public static readonly AdoptionPersona DevelopingBalanced =
            new AdoptionPersona("Developing - balanced", AdoptionBand.Developing, 39.5, 5, 2, 1);

        // --- Trialling ------------------------------------------------------------------------
        // 2 days (0.167), 1/day (0.200), 1 app (0.333) -> 8.3 + 6.0 + 6.7 = 21.0
        public static readonly AdoptionPersona TriallingCurious =
            new AdoptionPersona("Trialling - curious", AdoptionBand.Trialling, 21.0, 2, 1, 1);

        // 1 day (0.083), 2/day (0.400), 1 app (0.333) -> 4.2 + 12.0 + 6.7 = 22.8
        public static readonly AdoptionPersona TriallingOneOff =
            new AdoptionPersona("Trialling - one sitting only", AdoptionBand.Trialling, 22.8, 1, 2, 1);

        // --- Dormant (used it, then stopped) --------------------------------------------------
        public static readonly AdoptionPersona DormantRecentlyLapsed =
            new AdoptionPersona("Dormant - recently lapsed", AdoptionBand.Dormant, 0, 0, 0, 0, priorInteractions: 40, priorDaysAgo: 45);

        public static readonly AdoptionPersona DormantLongGone =
            new AdoptionPersona("Dormant - long gone", AdoptionBand.Dormant, 0, 0, 0, 0, priorInteractions: 9, priorDaysAgo: 150);

        // --- Never used -----------------------------------------------------------------------
        public static readonly AdoptionPersona NeverUsed =
            new AdoptionPersona("Never used", AdoptionBand.NeverUsed, 0, 0, 0, 0);

        /// <summary>A seat still assigned to a account that has been disabled - the cheapest reclaim there is.</summary>
        public static readonly AdoptionPersona NeverUsedDisabledAccount =
            new AdoptionPersona("Never used - disabled account", AdoptionBand.NeverUsed, 0, 0, 0, 0, accountEnabled: false);

        /// <summary>Every persona, for the generator's summary and for tests.</summary>
        public static readonly IReadOnlyList<AdoptionPersona> All = new[]
        {
            ChampionAllRound,
            ChampionEmerging,
            EstablishedBalanced,
            EstablishedFrequentShallow,
            EstablishedDeepNarrow,
            DevelopingBroadOccasional,
            DevelopingBalanced,
            TriallingCurious,
            TriallingOneOff,
            DormantRecentlyLapsed,
            DormantLongGone,
            NeverUsed,
            NeverUsedDisabledAccount,
        };

        /// <summary>
        /// The persona mix for a department at a given maturity, as (persona, relative weight).
        ///
        /// Every tier still contains some of every extreme: real tenants always have one enthusiast in
        /// the worst department and one untouched seat in the best, and a demo that shows otherwise
        /// teaches the wrong lesson about what the tool is for.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<AdoptionPersona, int>> MixFor(DepartmentMaturity maturity)
        {
            switch (maturity)
            {
                case DepartmentMaturity.Leading:
                    return Mix(
                        Pair(ChampionAllRound, 4),
                        Pair(ChampionEmerging, 5),
                        Pair(EstablishedBalanced, 5),
                        Pair(EstablishedFrequentShallow, 3),
                        Pair(EstablishedDeepNarrow, 3),
                        Pair(DevelopingBroadOccasional, 2),
                        Pair(DevelopingBalanced, 2),
                        Pair(TriallingCurious, 1),
                        Pair(DormantRecentlyLapsed, 1),
                        Pair(NeverUsed, 1));

                case DepartmentMaturity.Lagging:
                    return Mix(
                        Pair(ChampionEmerging, 1),
                        Pair(EstablishedDeepNarrow, 1),
                        Pair(DevelopingBalanced, 2),
                        Pair(TriallingCurious, 3),
                        Pair(TriallingOneOff, 3),
                        Pair(DormantRecentlyLapsed, 3),
                        Pair(DormantLongGone, 2),
                        Pair(NeverUsed, 5),
                        Pair(NeverUsedDisabledAccount, 1));

                default:
                    return Mix(
                        Pair(ChampionAllRound, 1),
                        Pair(ChampionEmerging, 2),
                        Pair(EstablishedBalanced, 3),
                        Pair(EstablishedFrequentShallow, 3),
                        Pair(EstablishedDeepNarrow, 3),
                        Pair(DevelopingBroadOccasional, 3),
                        Pair(DevelopingBalanced, 3),
                        Pair(TriallingCurious, 2),
                        Pair(TriallingOneOff, 2),
                        Pair(DormantRecentlyLapsed, 2),
                        Pair(DormantLongGone, 1),
                        Pair(NeverUsed, 2),
                        Pair(NeverUsedDisabledAccount, 1));
            }
        }

        /// <summary>Picks a persona from a weighted mix.</summary>
        public static AdoptionPersona Pick(IReadOnlyList<KeyValuePair<AdoptionPersona, int>> mix, Random random)
        {
            var total = mix.Sum(p => p.Value);
            if (total <= 0) return NeverUsed;

            var roll = random.Next(total);
            foreach (var entry in mix)
            {
                roll -= entry.Value;
                if (roll < 0) return entry.Key;
            }
            return mix[mix.Count - 1].Key;
        }

        private static KeyValuePair<AdoptionPersona, int> Pair(AdoptionPersona persona, int weight)
        {
            return new KeyValuePair<AdoptionPersona, int>(persona, weight);
        }

        private static IReadOnlyList<KeyValuePair<AdoptionPersona, int>> Mix(params KeyValuePair<AdoptionPersona, int>[] entries)
        {
            return entries;
        }
    }
}

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>How habitually a person uses Teams across the reporting window.</summary>
    public enum TeamsUserSegment
    {
        /// <summary>No activity at all in the window.</summary>
        Dormant = 0,

        /// <summary>Active on a small minority of the window's working days.</summary>
        Light = 1,

        /// <summary>Active on a meaningful share of working days, but not most of them.</summary>
        Regular = 2,

        /// <summary>Active on most working days - Teams is part of how they work.</summary>
        Power = 3,
    }

    /// <summary>Everything a headline judgement is derived from, so the derivation stays pure.</summary>
    public sealed class TeamsJudgementInputs
    {
        /// <summary>Distinct users with any Teams activity in the window.</summary>
        public int ActiveUsers { get; set; }

        /// <summary>Directory users the reach percentage is measured against.</summary>
        public int KnownUsers { get; set; }

        /// <summary>Share of chat messages sent in channels rather than private chats, 0-100.</summary>
        public double OpenCollaborationPct { get; set; }

        /// <summary>Mean meetings attended per active user over the window.</summary>
        public double MeetingsPerActiveUser { get; set; }

        /// <summary>Share of calls starting outside working hours or at the weekend, 0-100.</summary>
        public double AfterHoursPct { get; set; }

        /// <summary>Share of meeting organisation carried by the busiest tenth of organisers, 0-100.</summary>
        public double OrganiserConcentrationPct { get; set; }

        /// <summary>Teams with at least one channel message in the window.</summary>
        public int ActiveTeams { get; set; }

        /// <summary>Teams known to the database.</summary>
        public int TotalTeams { get; set; }

        /// <summary>Teams with no owner recorded - a governance risk.</summary>
        public int OwnerlessTeams { get; set; }

        /// <summary>False when the usage-report import is off, so reach cannot be judged.</summary>
        public bool UsageReportsAvailable { get; set; }

        /// <summary>False when the calls import is off, so meeting behaviour cannot be judged.</summary>
        public bool CallsAvailable { get; set; }

        /// <summary>False when Teams deep analytics is off, so governance cannot be judged.</summary>
        public bool TeamsAnalyticsAvailable { get; set; }
    }

    /// <summary>A short plain-English statement about the tenant, with the tone it should be shown in.</summary>
    /// <remarks>
    /// The camelCase naming strategy is not decoration. This type is serialised straight to the
    /// portal, which has no camelCase contract resolver configured, so without it the payload carries
    /// <c>Headline</c> / <c>Detail</c> / <c>Tone</c> and the Overview tab renders a row of blank cards
    /// - present, correctly counted, and empty. Every other model reaching the portal carries the
    /// same attribute; <c>TeamsExplorerApiContractTests</c> now asserts it for all of them.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsJudgement
    {
        public TeamsJudgement(string key, string tone, string headline, string detail)
        {
            Key = key;
            Tone = tone;
            Headline = headline;
            Detail = detail;
        }

        /// <summary>Stable identifier, for the UI's React key and for tests.</summary>
        public string Key { get; }

        /// <summary>One of <c>good</c>, <c>warning</c>, <c>critical</c> or <c>neutral</c>.</summary>
        public string Tone { get; }

        /// <summary>The finding, in one sentence.</summary>
        public string Headline { get; }

        /// <summary>Why it matters / what to do, in one sentence.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// The judgement layer of the Teams Explorer: segment thresholds, the band scale, concentration
    /// and the headline findings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately pure and free of SQL, EF and configuration, so every threshold on the page can be
    /// asserted in a unit test rather than eyeballed against a chart. The SQL builder reads the same
    /// constants, so a figure and the sentence describing it cannot disagree.
    /// </para>
    /// <para>
    /// The band boundaries mirror <c>ADOPTION_BANDS</c> in the portal's <c>GaugeRing.tsx</c>. They are
    /// duplicated rather than served from the API because the gauge must render before any data
    /// arrives; the pair is covered by a test that states the expected values, so a change on one side
    /// fails rather than silently drifting.
    /// </para>
    /// </remarks>
    public static class TeamsExplorerScoring
    {
        /// <summary>Upper bound (exclusive) of the Light segment, as a share of working days.</summary>
        public const double LightUpperBound = 0.20;

        /// <summary>Upper bound (inclusive) of the Regular segment, as a share of working days.</summary>
        public const double RegularUpperBound = 0.60;

        /// <summary>Below this percentage an adoption-style figure is "Needs attention".</summary>
        public const double NeedsAttentionUpperPct = 40;

        /// <summary>Below this percentage an adoption-style figure is "Progressing"; above it, "Healthy".</summary>
        public const double ProgressingUpperPct = 70;

        /// <summary>First hour of the assumed working day, inclusive (UTC unless offset by the caller).</summary>
        public const int WorkingDayStartHour = 8;

        /// <summary>First hour after the assumed working day, exclusive.</summary>
        public const int WorkingDayEndHour = 18;

        /// <summary>Share of organisers treated as "the busiest tenth" by <see cref="TopDecileShare"/>.</summary>
        public const double TopDecileFraction = 0.10;

        /// <summary>
        /// Working days (Mon-Fri) in a half-open UTC window. This is the denominator for the segment
        /// thresholds.
        /// </summary>
        /// <remarks>
        /// Calendar days would be the wrong denominator: almost nobody works seven days a week, so a
        /// perfectly engaged user would top out around 71% and the Power band would be nearly empty.
        /// Counting only weekdays makes "active on most of the days they were at work" mean what it
        /// says. It does mean a genuinely weekend-heavy shift pattern can exceed 100%, which
        /// <see cref="Segment"/> handles by clamping rather than by excluding weekend activity - the
        /// activity is real and should count.
        /// </remarks>
        public static int WorkingDaysBetween(DateTime fromUtc, DateTime toExclusiveUtc)
        {
            var days = 0;
            for (var day = fromUtc.Date; day < toExclusiveUtc.Date; day = day.AddDays(1))
            {
                if (day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday)
                {
                    days++;
                }
            }

            return days;
        }

        /// <summary>The segment a user falls into, from their active-day count.</summary>
        public static TeamsUserSegment Segment(int activeDays, int workingDays)
        {
            if (activeDays <= 0) return TeamsUserSegment.Dormant;
            if (workingDays <= 0) return TeamsUserSegment.Light;

            // Weekend activity can push this above 1.0; that is real activity and should not be
            // discarded, so clamp instead of excluding it.
            var ratio = Math.Min(1.0, (double)activeDays / workingDays);

            if (ratio < LightUpperBound) return TeamsUserSegment.Light;
            return ratio <= RegularUpperBound ? TeamsUserSegment.Regular : TeamsUserSegment.Power;
        }

        /// <summary>
        /// The smallest active-day count that lands a user in the <see cref="TeamsUserSegment.Power"/>
        /// segment for a window of the given length.
        /// </summary>
        /// <remarks>
        /// Found by searching <see cref="Segment"/> rather than by rearranging the threshold
        /// arithmetic, so the two can never disagree. The obvious closed form
        /// (<c>floor(workingDays * RegularUpperBound) + 1</c>) is wrong: 0.6 has no exact binary
        /// representation, so at five working days it computes 3 while <see cref="Segment"/> puts 3 of
        /// 5 days in Regular. The search is over at most a few hundred values and runs once per
        /// request.
        /// </remarks>
        public static int MinimumPowerDays(int workingDays)
        {
            if (workingDays <= 0) return 1;

            for (var days = 1; days <= workingDays; days++)
            {
                if (Segment(days, workingDays) == TeamsUserSegment.Power) return days;
            }

            return workingDays;
        }

        /// <summary>The display label for a segment.</summary>
        public static string SegmentLabel(TeamsUserSegment segment)
        {
            switch (segment)
            {
                case TeamsUserSegment.Dormant: return "Dormant";
                case TeamsUserSegment.Light: return "Light";
                case TeamsUserSegment.Regular: return "Regular";
                case TeamsUserSegment.Power: return "Power";
                default: return segment.ToString();
            }
        }

        /// <summary>What a segment means, in words, for the UI's definition tooltip.</summary>
        public static string SegmentDescription(TeamsUserSegment segment)
        {
            var lightPct = FormatPercent(LightUpperBound * 100);
            var regularPct = FormatPercent(RegularUpperBound * 100);

            switch (segment)
            {
                case TeamsUserSegment.Dormant:
                    return "No Teams activity at all in the window.";
                case TeamsUserSegment.Light:
                    return $"Active on fewer than {lightPct}% of the window's working days.";
                case TeamsUserSegment.Regular:
                    return $"Active on {lightPct}-{regularPct}% of the window's working days.";
                case TeamsUserSegment.Power:
                    return $"Active on more than {regularPct}% of the window's working days.";
                default:
                    return string.Empty;
            }
        }

        /// <summary>The band label for an adoption-style percentage.</summary>
        public static string BandLabel(double percent)
        {
            if (percent < NeedsAttentionUpperPct) return "Needs attention";
            return percent < ProgressingUpperPct ? "Progressing" : "Healthy";
        }

        /// <summary>
        /// The KPI tone for an adoption-style percentage, using the same boundaries as
        /// <see cref="BandLabel"/> so a card's colour and its caption cannot disagree.
        /// </summary>
        public static string BandTone(double percent)
        {
            if (percent < NeedsAttentionUpperPct) return "critical";
            return percent < ProgressingUpperPct ? "warning" : "good";
        }

        /// <summary>A percentage, or 0 when the denominator is zero (never a divide-by-zero or NaN).</summary>
        public static double Percentage(double numerator, double denominator)
        {
            if (denominator <= 0) return 0;
            return numerator / denominator * 100.0;
        }

        /// <summary>
        /// The share of a total carried by the busiest tenth of contributors, as a percentage.
        /// </summary>
        /// <remarks>
        /// This is the "is the meeting load spread, or carried by a handful of people?" measure. With
        /// perfectly even contribution it returns 10%; the closer it is to 100% the more the activity
        /// is one small group's. The decile is rounded UP to at least one contributor, so the measure
        /// is defined for very small tenants rather than silently returning zero.
        /// </remarks>
        public static double TopDecileShare(IEnumerable<long> values)
        {
            if (values == null) return 0;

            var ordered = values.Where(v => v > 0).OrderByDescending(v => v).ToList();
            if (ordered.Count == 0) return 0;

            var total = ordered.Sum();
            if (total <= 0) return 0;

            var take = (int)Math.Ceiling(ordered.Count * TopDecileFraction);
            if (take < 1) take = 1;

            return Percentage(ordered.Take(take).Sum(), total);
        }

        /// <summary>
        /// The headline findings for the Overview tab.
        /// </summary>
        /// <remarks>
        /// Only findings the available data can actually support are produced: a tenant with the calls
        /// import switched off is not told its meeting culture is healthy. Each finding names the
        /// figure it came from, so the reader can check it against the charts below rather than taking
        /// the sentence on trust.
        /// </remarks>
        public static List<TeamsJudgement> Judgements(TeamsJudgementInputs inputs)
        {
            var findings = new List<TeamsJudgement>();
            if (inputs == null) return findings;

            if (inputs.UsageReportsAvailable && inputs.KnownUsers > 0)
            {
                var reach = Percentage(inputs.ActiveUsers, inputs.KnownUsers);
                findings.Add(new TeamsJudgement(
                    "reach",
                    BandTone(reach),
                    $"{FormatPercent(reach)}% of known users used Teams in this period.",
                    $"{inputs.ActiveUsers:N0} of {inputs.KnownUsers:N0} directory users. "
                    + $"On the adoption scale that is \"{BandLabel(reach).ToLowerInvariant()}\"."));

                findings.Add(OpenCollaborationFinding(inputs.OpenCollaborationPct));
            }

            if (inputs.CallsAvailable)
            {
                findings.Add(MeetingLoadFinding(inputs));
            }

            if (inputs.TeamsAnalyticsAvailable && inputs.TotalTeams > 0)
            {
                findings.Add(GovernanceFinding(inputs));
            }

            return findings;
        }

        private static TeamsJudgement OpenCollaborationFinding(double openCollaborationPct)
        {
            // A low share means most conversation is happening in private chats, where it is invisible
            // to everyone who joins later. That is a knowledge-retention problem, not a usage problem,
            // so it is deliberately NOT scored on the adoption bands.
            var tone = openCollaborationPct < 15 ? "warning" : "neutral";

            return new TeamsJudgement(
                "open-collaboration",
                tone,
                $"{FormatPercent(openCollaborationPct)}% of chat messages were posted in channels.",
                openCollaborationPct < 15
                    ? "Most conversation is happening in private chats, so it is invisible to anyone who "
                      + "joins the work later. Channel posts stay searchable for the whole team."
                    : "Channel posts stay searchable for the whole team, unlike private chats.");
        }

        private static TeamsJudgement MeetingLoadFinding(TeamsJudgementInputs inputs)
        {
            var afterHours = inputs.AfterHoursPct;
            var concentration = inputs.OrganiserConcentrationPct;

            if (afterHours >= 20)
            {
                return new TeamsJudgement(
                    "after-hours",
                    "warning",
                    $"{FormatPercent(afterHours)}% of calls started outside working hours or at the weekend.",
                    "Sustained out-of-hours meeting load is a wellbeing signal worth looking at by "
                    + "department. Note that call times are recorded in UTC, so a genuinely global "
                    + "tenant will read high here.");
            }

            if (concentration >= 60)
            {
                return new TeamsJudgement(
                    "organiser-concentration",
                    "warning",
                    $"The busiest tenth of organisers ran {FormatPercent(concentration)}% of all meetings.",
                    "Meeting organisation is concentrated in a small group. That is normal for a "
                    + "coordination function, and a bottleneck anywhere else.");
            }

            return new TeamsJudgement(
                "meeting-load",
                "neutral",
                $"Active users attended {inputs.MeetingsPerActiveUser:N1} meetings each on average.",
                "Compare this against the department breakdown: a tenant-wide average hides the teams "
                + "that are actually saturated.");
        }

        private static TeamsJudgement GovernanceFinding(TeamsJudgementInputs inputs)
        {
            if (inputs.OwnerlessTeams > 0)
            {
                return new TeamsJudgement(
                    "ownerless-teams",
                    "critical",
                    $"{inputs.OwnerlessTeams:N0} of {inputs.TotalTeams:N0} teams have no recorded owner.",
                    "An ownerless team cannot be governed: nobody can approve membership, manage its "
                    + "files or archive it when the work ends.");
            }

            var dormant = inputs.TotalTeams - inputs.ActiveTeams;
            var activePct = Percentage(inputs.ActiveTeams, inputs.TotalTeams);

            return new TeamsJudgement(
                "team-sprawl",
                dormant > inputs.ActiveTeams ? "warning" : "good",
                $"{inputs.ActiveTeams:N0} of {inputs.TotalTeams:N0} teams saw a channel message "
                + $"({FormatPercent(activePct)}%).",
                dormant > inputs.ActiveTeams
                    ? $"{dormant:N0} teams were silent for the whole period. Dormant teams still hold "
                      + "files and members, so they are a real governance and storage cost."
                    : "Most teams are in genuine use, so sprawl is not the immediate problem here.");
        }

        /// <summary>A percentage to one decimal place, dropping a trailing ".0".</summary>
        public static string FormatPercent(double value)
        {
            var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            return rounded % 1 == 0
                ? rounded.ToString("0", CultureInfo.InvariantCulture)
                : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}

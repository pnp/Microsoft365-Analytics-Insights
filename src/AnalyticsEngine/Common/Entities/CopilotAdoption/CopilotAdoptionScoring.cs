using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Every adoption calculation in this feature, as pure functions over plain inputs.
    ///
    /// This is deliberately the <b>only</b> implementation of the maths. Nothing is computed in SQL
    /// that is also computed here, because the output of this tool is used to justify licence spend
    /// and to decide who gets training: a rule that exists twice will eventually disagree with itself,
    /// and the version an executive sees would then depend on which screen they opened. Keeping it in
    /// C# also means it can be unit-tested against hand-written inputs, and reused verbatim by a
    /// scheduled e-mail report later without lifting any of it out of a controller.
    ///
    /// The one exception is <see cref="BuildOpportunityScoreSql"/>, which emits a SQL expression for
    /// the same formula so the database can rank hundreds of thousands of unlicensed users and return
    /// only the top candidates. It is generated from the same <see cref="CopilotAdoptionOptions"/>
    /// instance, and the returned rows are always re-scored here before display, so the SQL is a
    /// ranking aid rather than a second source of truth.
    /// </summary>
    public static class CopilotAdoptionScoring
    {
        /// <summary>Score built from our own Copilot audit-log import.</summary>
        public const string SignalSourceAudit = "audit";

        /// <summary>Score built from Microsoft's per-user Copilot usage report.</summary>
        public const string SignalSourceUsageReport = "usageReport";

        #region Licensed-user engagement

        /// <summary>
        /// Working days available in the window - the denominator a fully engaged user is measured
        /// against. Calendar days would be the wrong denominator: someone using Copilot every single
        /// working day would top out around 71% and look like a partial adopter.
        /// </summary>
        public static double AvailableWorkingDays(CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var windowDays = Math.Max(1, o.WindowDays);
            var perWeek = o.WorkingDaysPerWeek <= 0 ? 5 : o.WorkingDaysPerWeek;
            return windowDays * perWeek / 7d;
        }

        /// <summary>
        /// Days of use inside the window that earn full marks for frequency. Reported to the user next
        /// to their actual active days so "62%" is never an unexplained number.
        /// </summary>
        public static double TargetActiveDays(CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var ratio = o.FrequencyTargetRatio <= 0 ? 1 : o.FrequencyTargetRatio;
            return Math.Max(1d, AvailableWorkingDays(o) * ratio);
        }

        /// <summary>
        /// Turns one licensed user's raw signals into a scored, banded, actionable row.
        /// </summary>
        /// <param name="row">The user's raw counters, straight from the database.</param>
        /// <param name="windowStartUtc">Start of the reporting window (inclusive).</param>
        /// <param name="nowUtc">"Now", used for the days-since-last-use figure. Passed in so tests are deterministic.</param>
        /// <param name="auditAvailable">
        /// Whether the Copilot audit-log import supplied data. When it did not, Microsoft's per-user
        /// usage report is used instead - otherwise every user would score zero and the entire licensed
        /// population would be reported as "never used", which is both wrong and expensive.
        /// </param>
        /// <param name="options">Tuning; <see cref="CopilotAdoptionOptions.Default"/> when null.</param>
        public static LicensedUserAdoptionRow Score(
            LicensedUserUsageRow row,
            DateTime windowStartUtc,
            DateTime nowUtc,
            bool auditAvailable,
            CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            // Prefer our own audit data: it is bucketed to exactly the window that was asked for.
            // Microsoft's report is the fallback, used in two cases: when the audit import supplied
            // nothing at all, and - just as importantly - when it supplied nothing *for this user*
            // while Microsoft says they were active. The second case is the dangerous one: an audit
            // import that is lagging or partially failed would otherwise report active people as
            // "never used", and this list is used to decide whose seat gets taken away. The choice is
            // recorded on the row rather than silently applied, because the report's window is
            // Microsoft's own rather than the one that was requested here.
            var auditHasSignal = row.Interactions > 0 || row.ActiveDays > 0;
            var reportHasSignal = (row.ReportActiveDays ?? 0) > 0 || (row.ReportPrompts ?? 0) > 0;
            var useReport = (!auditAvailable || !auditHasSignal) && reportHasSignal;

            var activeDays = useReport ? (row.ReportActiveDays ?? 0) : row.ActiveDays;
            var interactions = useReport ? (row.ReportPrompts ?? 0) : row.Interactions;
            var appsUsed = useReport ? (row.ReportAppsUsed ?? 0) : row.AppsUsed;
            var lastUse = useReport
                ? row.ReportLastActivityUtc
                : (row.LastInteractionUtc ?? row.ReportLastActivityUtc);

            var targetActiveDays = TargetActiveDays(o);
            var frequency = Ratio(activeDays, targetActiveDays);
            var depth = activeDays > 0
                ? Ratio((double)interactions / activeDays, o.DepthTargetInteractionsPerActiveDay)
                : 0d;
            var breadth = Ratio(appsUsed, o.BreadthTargetApps);

            var weightSum = o.FrequencyWeight + o.DepthWeight + o.BreadthWeight;
            var score = weightSum <= 0
                ? 0d
                : (frequency * o.FrequencyWeight + depth * o.DepthWeight + breadth * o.BreadthWeight)
                  / weightSum * 100d;

            var usedInWindow = interactions > 0 || activeDays > 0;
            // "Ever" means "inside the history window we actually queried" - see
            // CopilotAdoptionOptions.HistoryDays. Prior audit interactions are the primary signal; a
            // report last-activity date before the window covers the report-only case.
            var usedBeforeWindow = row.PriorInteractions > 0
                || (lastUse.HasValue && lastUse.Value < windowStartUtc)
                || (row.FirstInteractionUtc.HasValue && row.FirstInteractionUtc.Value < windowStartUtc);

            var band = BandFor(score, usedInWindow, usedBeforeWindow, o);

            var scored = new LicensedUserAdoptionRow
            {
                UserId = row.UserId,
                UserPrincipalName = row.UserPrincipalName,
                Mail = row.Mail,
                Department = row.Department,
                JobTitle = row.JobTitle,
                Country = row.Country,
                OfficeLocation = row.OfficeLocation,
                CompanyName = row.CompanyName,
                ManagerUserPrincipalName = row.ManagerUserPrincipalName,
                AccountEnabled = row.AccountEnabled,
                SeatLicences = row.SeatLicences,

                Interactions = interactions,
                ActiveDays = activeDays,
                ExpectedActiveDays = Round(targetActiveDays, 1),
                AppsUsed = appsUsed,
                AgentsUsed = row.AgentsUsed,
                CoworkInteractions = row.CoworkInteractions,
                UsedCowork = row.CoworkInteractions > 0,
                FirstInteractionUtc = row.FirstInteractionUtc,
                LastInteractionUtc = lastUse,
                DaysSinceLastUse = lastUse.HasValue
                    ? (int?)Math.Max(0, (int)(nowUtc.Date - lastUse.Value.Date).TotalDays)
                    : null,

                ReportPrompts = row.ReportPrompts,
                ReportActiveDays = row.ReportActiveDays,
                ReportLastActivityUtc = row.ReportLastActivityUtc,

                FrequencyScore = Round(frequency * 100d, 1),
                DepthScore = Round(depth * 100d, 1),
                BreadthScore = Round(breadth * 100d, 1),
                AdoptionScore = Round(score, 1),
                Band = band,
                BandName = BandDisplayName(band),
                SignalSource = useReport ? SignalSourceUsageReport : SignalSourceAudit,
            };

            scored.RecommendedActionCode = RecommendedActionCode(scored);
            scored.RecommendedActionLabel = ActionLabel(scored.RecommendedActionCode);
            scored.RecommendedAction = RecommendedAction(scored, o);
            return scored;
        }

        /// <summary>
        /// Which band a score falls in.
        ///
        /// Zero-score users are split into two very different populations before the thresholds are
        /// applied at all: someone who has never used Copilot needs onboarding (or has a seat that
        /// should go to someone else), whereas someone who used it and stopped needs a conversation
        /// about what went wrong. Averaging them into one "not using it" bucket hides both problems.
        /// </summary>
        public static AdoptionBand BandFor(
            double score,
            bool usedInWindow,
            bool usedBeforeWindow,
            CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            if (!usedInWindow)
            {
                return usedBeforeWindow ? AdoptionBand.Dormant : AdoptionBand.NeverUsed;
            }

            if (score >= o.ChampionScore) return AdoptionBand.Champion;
            if (score >= o.EstablishedScore) return AdoptionBand.Established;
            if (score >= o.DevelopingScore) return AdoptionBand.Developing;
            return AdoptionBand.Trialling;
        }

        /// <summary>Band name as shown in the UI, the charts and the CSV - one definition, so they agree.</summary>
        public static string BandDisplayName(AdoptionBand band)
        {
            switch (band)
            {
                case AdoptionBand.NeverUsed: return "Never used";
                case AdoptionBand.Dormant: return "Dormant";
                case AdoptionBand.Trialling: return "Trialling";
                case AdoptionBand.Developing: return "Developing";
                case AdoptionBand.Established: return "Established";
                case AdoptionBand.Champion: return "Champion";
                default: return band.ToString();
            }
        }

        /// <summary>All bands worst-first, so a distribution chart always shows every bucket - including
        /// the empty ones, which are themselves informative.</summary>
        public static IReadOnlyList<AdoptionBand> AllBands { get; } = new[]
        {
            AdoptionBand.NeverUsed,
            AdoptionBand.Dormant,
            AdoptionBand.Trialling,
            AdoptionBand.Developing,
            AdoptionBand.Established,
            AdoptionBand.Champion,
        };

        /// <summary>
        /// True when the user has made Copilot part of their working week. Used for the "habit rate"
        /// headline, which is the figure that actually correlates with realised value - "has used it at
        /// least once" is easy to hit and tells an executive nothing.
        /// </summary>
        public static bool IsHabitual(AdoptionBand band)
        {
            return band == AdoptionBand.Established || band == AdoptionBand.Champion;
        }

        #region Habit-formation buckets

        /// <summary>
        /// Restates a per-window figure as a per-month one.
        ///
        /// The reporting period is adjustable, so any rate quoted "per user" silently changes meaning
        /// when the reader changes the period drop-down unless it is normalised. Used for active days
        /// and for interaction volumes alike - it is a linear rescale, not a days-specific rule.
        /// </summary>
        public static double NormaliseToMonth(
            double valueInWindow,
            int windowDays,
            CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var days = Math.Max(1, windowDays);
            return valueInWindow * o.HabitBucketNormalisationDays / (double)days;
        }

        /// <summary>
        /// Active days in the window, restated as active days per month.
        ///
        /// Without this, "11+ active days" would mean a near-daily user over a 28-day window and a
        /// once-a-fortnight user over a 180-day one, and the same tile would silently change meaning
        /// when the reader changed the period drop-down.
        /// </summary>
        public static double NormalisedActiveDaysPerMonth(
            double activeDays,
            int windowDays,
            CopilotAdoptionOptions options = null)
        {
            return NormaliseToMonth(activeDays, windowDays, options);
        }

        /// <summary>
        /// Habit bucket for a normalised active-days-per-month figure.
        ///
        /// The normalised value is fractional (12 days in a 90-day window is 3.73 days a month), so it
        /// is rounded to whole days before bucketing - otherwise the tile captions ("1-5 active days a
        /// month") would not exactly describe the comparison being made, and a user on 5.6 days would
        /// sit in a bucket whose label excludes them. Any activity at all rounds up to at least one
        /// day, so a single interaction in a 180-day window is Infrequent rather than unbucketed.
        ///
        /// Zero maps to null: a user with no activity is not "infrequent", they are in the reclaim
        /// pile, and merging the two hides the more expensive problem.
        /// </summary>
        public static string HabitBucketFor(double normalisedActiveDays, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            if (normalisedActiveDays <= 0) return null;

            var days = Math.Max(1, (int)Math.Round(normalisedActiveDays, MidpointRounding.AwayFromZero));

            if (days >= o.HabitDailyMinDays) return "Daily";
            if (days >= o.HabitFrequentMinDays) return "Frequent";
            if (days >= o.HabitModerateMinDays) return "Moderate";
            return "Infrequent";
        }

        /// <summary>Bucket names, least engaged first, so a habit strip always shows every bucket.</summary>
        public static IReadOnlyList<string> AllHabitBuckets { get; } = new[]
        {
            "Infrequent", "Moderate", "Frequent", "Daily",
        };

        /// <summary>The bucket's day range in plain English, e.g. "6-10 active days a month".</summary>
        public static string HabitBucketRangeLabel(string bucket, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var moderate = (int)Math.Round(o.HabitModerateMinDays, MidpointRounding.AwayFromZero);
            var frequent = (int)Math.Round(o.HabitFrequentMinDays, MidpointRounding.AwayFromZero);
            var daily = (int)Math.Round(o.HabitDailyMinDays, MidpointRounding.AwayFromZero);

            switch (bucket)
            {
                case "Infrequent": return $"1-{Math.Max(1, moderate - 1)} active days a month";
                case "Moderate": return $"{moderate}-{Math.Max(moderate, frequent - 1)} active days a month";
                case "Frequent": return $"{frequent}-{Math.Max(frequent, daily - 1)} active days a month";
                case "Daily": return $"{daily}+ active days a month";
                default: return string.Empty;
            }
        }

        #endregion

        /// <summary>
        /// The single next step for this user, in plain English. Exported in the CSV so the list can be
        /// handed to a department lead and acted on without further interpretation.
        ///
        /// On screen the prose is shown once per action group rather than once per row - see
        /// <see cref="RecommendedActionCode"/> and <see cref="ActionDescription"/>. In a CSV, where a
        /// reader takes one row at a time and may sort or filter it arbitrarily, the full sentence on
        /// every row is worth the repetition.
        /// </summary>
        public static string RecommendedAction(LicensedUserAdoptionRow row, CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            switch (row.Band)
            {
                case AdoptionBand.NeverUsed:
                    return $"Reclaim or onboard - no Copilot activity in the last {o.HistoryDays} days. "
                         + "Confirm the seat is still needed before renewal.";

                case AdoptionBand.Dormant:
                    var since = row.DaysSinceLastUse.HasValue
                        ? $"last used it {row.DaysSinceLastUse.Value} days ago"
                        : "has used it in the past";
                    return $"Re-engage - {since} but not once in this period. "
                         + "Ask what stopped and offer a refresher, or reassign the seat.";

                case AdoptionBand.Trialling:
                    return "Coach - occasional use only. Target one repeatable Copilot habit in the app they "
                         + "already live in.";

                case AdoptionBand.Developing:
                    return row.BreadthScore < 34
                        ? $"Broaden - building a habit but only in {AppsPhrase(row.AppsUsed)}. Introduce a second "
                          + "surface such as Outlook or Teams meeting recaps."
                        : "Grow - a habit is forming. A short scenario-based session should move them to daily use.";

                case AdoptionBand.Established:
                    return row.BreadthScore < 50
                        ? $"Broaden - solid regular use, but confined to {AppsPhrase(row.AppsUsed)}; "
                          + "showing them one more surface is the cheapest remaining gain."
                        : "Sustain - Copilot is part of their working week. No action needed.";

                case AdoptionBand.Champion:
                    // A Champion who only ever works in one surface is still leaving value on the
                    // table, and is the cheapest possible win - so say so rather than congratulating
                    // them and moving on.
                    return row.BreadthScore < 50
                        ? $"Recruit as an advocate, and broaden - deep, frequent use but only in "
                          + $"{AppsPhrase(row.AppsUsed)}. Showing them one more surface is the cheapest gain available."
                        : "Recruit as an advocate - among your deepest users. Ask them to run a peer session "
                          + "for their department.";

                default:
                    return string.Empty;
            }
        }

        #region Recommended-action catalogue

        /// <summary>
        /// The stable action codes. Deliberately a small closed set: an admin planning an enablement
        /// programme needs to be able to say "these 76 people need coaching", which only works if the
        /// action is a value they can group and count by rather than a sentence.
        /// </summary>
        public static class AdoptionActionCodes
        {
            public const string Reclaim = "reclaim";
            public const string Reengage = "reengage";
            public const string Coach = "coach";
            public const string Broaden = "broaden";
            public const string Grow = "grow";
            public const string Sustain = "sustain";
            public const string Advocate = "advocate";
        }

        /// <summary>All action codes in the order they should be worked through - cheapest saving first.</summary>
        public static IReadOnlyList<string> AllActionCodes { get; } = new[]
        {
            AdoptionActionCodes.Reclaim,
            AdoptionActionCodes.Reengage,
            AdoptionActionCodes.Coach,
            AdoptionActionCodes.Broaden,
            AdoptionActionCodes.Grow,
            AdoptionActionCodes.Sustain,
            AdoptionActionCodes.Advocate,
        };

        /// <summary>
        /// Which action this user needs, as a code. Shares its branching with
        /// <see cref="RecommendedAction"/> so the tag on screen can never disagree with the sentence in
        /// the CSV.
        /// </summary>
        public static string RecommendedActionCode(LicensedUserAdoptionRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));

            switch (row.Band)
            {
                case AdoptionBand.NeverUsed: return AdoptionActionCodes.Reclaim;
                case AdoptionBand.Dormant: return AdoptionActionCodes.Reengage;
                case AdoptionBand.Trialling: return AdoptionActionCodes.Coach;
                case AdoptionBand.Developing:
                    return row.BreadthScore < 34 ? AdoptionActionCodes.Broaden : AdoptionActionCodes.Grow;
                case AdoptionBand.Established:
                    return row.BreadthScore < 50 ? AdoptionActionCodes.Broaden : AdoptionActionCodes.Sustain;
                case AdoptionBand.Champion:
                    return AdoptionActionCodes.Advocate;
                default: return string.Empty;
            }
        }

        /// <summary>Short display label for an action code.</summary>
        public static string ActionLabel(string code)
        {
            switch (code)
            {
                case AdoptionActionCodes.Reclaim: return "Reclaim or onboard";
                case AdoptionActionCodes.Reengage: return "Re-engage";
                case AdoptionActionCodes.Coach: return "Coach";
                case AdoptionActionCodes.Broaden: return "Broaden";
                case AdoptionActionCodes.Grow: return "Grow";
                case AdoptionActionCodes.Sustain: return "Sustain";
                case AdoptionActionCodes.Advocate: return "Recruit as advocate";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// What the action means and why these users qualify for it - stated once per action rather
        /// than repeated on every row that shares it.
        /// </summary>
        public static string ActionDescription(string code, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            switch (code)
            {
                case AdoptionActionCodes.Reclaim:
                    return $"No Copilot activity at all in the last {o.HistoryDays} days. Confirm the seat is "
                         + "still needed before renewal; if it is, this person has never been onboarded and "
                         + "the seat has produced nothing so far.";

                case AdoptionActionCodes.Reengage:
                    return "Used Copilot before this period but not once inside it. Someone who tried it and "
                         + "stopped is a different problem from someone who never started - ask what stopped, "
                         + "offer a refresher, or reassign the seat.";

                case AdoptionActionCodes.Coach:
                    return $"Occasional use only (engagement below {o.DevelopingScore}). The cheapest move is "
                         + "one repeatable habit in the app they already live in, rather than a general "
                         + "Copilot training session.";

                case AdoptionActionCodes.Broaden:
                    return "Real, regular use - but almost entirely in a single Copilot surface. Introducing "
                         + "one more surface (Outlook summaries, Teams meeting recaps) is the cheapest "
                         + "remaining gain for these users, because they have already accepted Copilot and "
                         + "simply have not been shown where else it works.";

                case AdoptionActionCodes.Grow:
                    return $"Engagement between {o.DevelopingScore} and {o.EstablishedScore} across more than "
                         + "one surface. A short scenario-based session aimed at their actual job is what "
                         + "moves this group to daily use.";

                case AdoptionActionCodes.Sustain:
                    return $"Engagement at or above {o.EstablishedScore} across multiple surfaces - Copilot is "
                         + "part of their working week. No action needed; these are the seats that are paying "
                         + "for themselves.";

                case AdoptionActionCodes.Advocate:
                    return $"Engagement at or above {o.ChampionScore} - among your deepest users. Ask them to "
                         + "run a peer session for their own department, which converts better than centrally "
                         + "run training. If their breadth score is low they are still worth showing one more "
                         + "surface.";

                default: return string.Empty;
            }
        }

        #endregion

        #endregion

        #region Agent inventory

        /// <summary>
        /// The verdict on an agent: keep it, review it, retire it - or leave it alone because it is
        /// too new to judge.
        ///
        /// The "New" exemption is the important one. A brand-new agent with two users is not failing,
        /// it has not started, and an inventory review that retires it on that evidence is how an agent
        /// programme gets strangled in its first month.
        /// </summary>
        public static AgentUsageRow ScoreAgent(
            AgentUsageQueryRow row,
            DateTime nowUtc,
            CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            var daysSinceLastUse = row.LastUsedUtc.HasValue
                ? (int?)Math.Max(0, (int)(nowUtc.Date - row.LastUsedUtc.Value.Date).TotalDays)
                : null;

            var daysSinceFirstUse = row.FirstUsedUtc.HasValue
                ? (int?)Math.Max(0, (int)(nowUtc.Date - row.FirstUsedUtc.Value.Date).TotalDays)
                : null;

            var scored = new AgentUsageRow
            {
                AgentId = row.AgentId,
                Name = row.Name,
                AgentKey = row.AgentKey,
                IsCustomAgent = row.IsCustomAgent,
                Interactions = row.Interactions,
                Users = row.Users,
                LicensedUsers = row.LicensedUsers,
                ActiveDays = row.ActiveDays,
                AppsUsed = row.AppsUsed,
                InteractionsPerUser = row.Users <= 0
                    ? 0
                    : Round(row.Interactions / (double)row.Users, 1),
                FirstUsedUtc = row.FirstUsedUtc,
                LastUsedUtc = row.LastUsedUtc,
                DaysSinceLastUse = daysSinceLastUse,
            };

            scored.Health = AgentHealthFor(daysSinceFirstUse, daysSinceLastUse, row.Users, o);
            scored.HealthName = AgentHealthDisplayName(scored.Health);
            scored.HealthReason = AgentHealthReason(scored, o);
            return scored;
        }

        /// <summary>The health rule on its own, so it can be tested without building a row.</summary>
        public static AgentHealth AgentHealthFor(
            int? daysSinceFirstUse,
            int? daysSinceLastUse,
            int users,
            CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            // Never used at all, or no dates recorded: nothing to judge it on but its age.
            if (!daysSinceLastUse.HasValue)
            {
                return daysSinceFirstUse.HasValue && daysSinceFirstUse.Value <= o.AgentNewDays
                    ? AgentHealth.New
                    : AgentHealth.Retire;
            }

            // Checked before the inactivity rules on purpose - see the remarks above.
            if (daysSinceFirstUse.HasValue && daysSinceFirstUse.Value <= o.AgentNewDays)
            {
                return AgentHealth.New;
            }

            if (daysSinceLastUse.Value >= o.AgentRetireInactiveDays) return AgentHealth.Retire;
            if (daysSinceLastUse.Value >= o.AgentReviewInactiveDays) return AgentHealth.Review;

            // Current, but used by so few people that it is likely still its author testing it.
            return users < o.AgentMinUsers ? AgentHealth.Review : AgentHealth.Keep;
        }

        public static string AgentHealthDisplayName(AgentHealth health)
        {
            switch (health)
            {
                case AgentHealth.Keep: return "Keep";
                case AgentHealth.New: return "New";
                case AgentHealth.Review: return "Review";
                case AgentHealth.Retire: return "Retire";
                default: return health.ToString();
            }
        }

        /// <summary>All health states, worst first, so a breakdown always shows every bucket.</summary>
        public static IReadOnlyList<AgentHealth> AllAgentHealthStates { get; } = new[]
        {
            AgentHealth.Retire, AgentHealth.Review, AgentHealth.New, AgentHealth.Keep,
        };

        /// <summary>Why this agent got this verdict - the same explain-yourself rule the rest of the tool follows.</summary>
        public static string AgentHealthReason(AgentUsageRow row, CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            switch (row.Health)
            {
                case AgentHealth.New:
                    return $"First seen within the last {o.AgentNewDays} days. Too new to judge - give it "
                         + "time to gain adoption before reviewing it.";

                case AgentHealth.Retire:
                    return row.DaysSinceLastUse.HasValue
                        ? $"Not used for {row.DaysSinceLastUse.Value} days ({o.AgentRetireInactiveDays}+ is the "
                          + "retirement line). Confirm with its owner, then remove it."
                        : "No recorded use at all. Confirm with its owner, then remove it.";

                case AgentHealth.Review:
                    if (row.DaysSinceLastUse.HasValue && row.DaysSinceLastUse.Value >= o.AgentReviewInactiveDays)
                    {
                        return $"Going quiet - last used {row.DaysSinceLastUse.Value} days ago. Worth asking "
                             + "whether it is still needed before it drifts into the retire pile.";
                    }
                    return $"Still in use, but only by {row.Users} "
                         + (row.Users == 1 ? "person" : "people")
                         + $" - below the {o.AgentMinUsers} needed to call it adopted. Often this is the author "
                         + "testing it, or an agent that was never announced to the people it was built for.";

                case AgentHealth.Keep:
                    return $"Used within the last {o.AgentReviewInactiveDays} days by {row.Users} people. "
                         + "Genuinely adopted - keep supporting it.";

                default: return string.Empty;
            }
        }

        #endregion

        #region Usage concentration

        /// <summary>
        /// The cohorts the usage distribution is cut into, heaviest first. Percentile boundaries rather
        /// than fixed counts, so the shape is comparable between a 50-seat tenant and a 50,000-seat one.
        /// </summary>
        public static IReadOnlyList<Tuple<string, double>> ConcentrationCohorts { get; } =
            new[]
            {
                Tuple.Create("Top 10%", 0.10),
                Tuple.Create("Next 15%", 0.15),
                Tuple.Create("Next 25%", 0.25),
                Tuple.Create("Bottom 50%", 0.50),
            };

        /// <summary>
        /// How concentrated Copilot usage is across the people who actually use it.
        ///
        /// Copilot usage is almost always a power law, and "40% adoption spread evenly" and "40%
        /// adoption where a tenth of them do most of it" are completely different situations that
        /// produce the same adoption percentage. The first is a programme working; the second is a
        /// programme propped up by a handful of enthusiasts, and it will collapse when they move team.
        ///
        /// Only active users are ranked. Including idle seats would put every one of them in the bottom
        /// cohort at zero and turn every tenant's chart into the same shape.
        /// </summary>
        public static List<AdoptionConcentrationBand> Concentration(IEnumerable<long> interactionsPerActiveUser)
        {
            var ranked = (interactionsPerActiveUser ?? Enumerable.Empty<long>())
                .Where(i => i > 0)
                .OrderByDescending(i => i)
                .ToList();

            var bands = new List<AdoptionConcentrationBand>();
            if (ranked.Count == 0) return bands;

            var total = ranked.Sum();
            var taken = 0;

            for (var i = 0; i < ConcentrationCohorts.Count; i++)
            {
                var cohort = ConcentrationCohorts[i];

                // The last cohort takes whatever is left, so rounding can never lose or duplicate a user.
                var size = i == ConcentrationCohorts.Count - 1
                    ? ranked.Count - taken
                    : Math.Min(ranked.Count - taken, (int)Math.Round(ranked.Count * cohort.Item2, MidpointRounding.AwayFromZero));

                if (size <= 0) continue;

                var slice = ranked.Skip(taken).Take(size).ToList();
                var sliceTotal = slice.Sum();

                bands.Add(new AdoptionConcentrationBand
                {
                    Label = cohort.Item1,
                    Users = size,
                    Interactions = sliceTotal,
                    SharePct = Percentage(sliceTotal, total),
                    InteractionsPerUser = Round(sliceTotal / (double)size, 1),
                });

                taken += size;
            }

            return bands;
        }

        #endregion

        #region Licence opportunity (unlicensed users)

        /// <summary>
        /// Scores an unlicensed user as a candidate for a Copilot seat.
        ///
        /// The components are weighted so that <b>evidence beats inference</b>: someone already using
        /// Copilot Chat without a seat has demonstrated demand for Copilot itself, which is a far
        /// stronger argument than "sends a lot of email". The Microsoft 365 activity signals still
        /// matter - they are what identifies the heavy knowledge workers who would benefit but have
        /// never had the chance to try it - but they cannot on their own reach the recommendation
        /// threshold from a standing start unless the user is heavy across several workloads.
        /// </summary>
        public static LicenceOpportunityRow ScoreOpportunity(
            UnlicensedUserSignalRow row,
            CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            var copilot = Ratio(row.UnlicensedCopilotInteractions, o.OpportunityCopilotTarget);
            var collaboration = Ratio(row.TeamsMessages + row.TeamsMeetings, o.OpportunityCollaborationTarget);
            var email = Ratio(row.EmailsSent + row.EmailsRead, o.OpportunityEmailTarget);
            var documents = Ratio(row.FilesViewedOrEdited, o.OpportunityDocumentTarget);

            var score = copilot * o.OpportunityUnlicensedCopilotWeight
                      + collaboration * o.OpportunityCollaborationWeight
                      + email * o.OpportunityEmailWeight
                      + documents * o.OpportunityDocumentWeight;

            var scored = new LicenceOpportunityRow
            {
                UserId = row.UserId,
                UserPrincipalName = row.UserPrincipalName,
                Mail = row.Mail,
                Department = row.Department,
                JobTitle = row.JobTitle,
                Country = row.Country,
                OfficeLocation = row.OfficeLocation,
                CompanyName = row.CompanyName,
                ManagerUserPrincipalName = row.ManagerUserPrincipalName,

                UnlicensedCopilotInteractions = row.UnlicensedCopilotInteractions,
                UnlicensedCopilotActiveDays = row.UnlicensedCopilotActiveDays,
                LastCopilotInteractionUtc = row.LastCopilotInteractionUtc,
                TeamsMessages = row.TeamsMessages,
                TeamsMeetings = row.TeamsMeetings,
                EmailsSent = row.EmailsSent,
                EmailsRead = row.EmailsRead,
                FilesViewedOrEdited = row.FilesViewedOrEdited,
                LastM365ActivityUtc = row.LastM365ActivityUtc,

                CopilotDemandScore = Round(copilot * 100d, 1),
                CollaborationScore = Round(collaboration * 100d, 1),
                EmailScore = Round(email * 100d, 1),
                DocumentScore = Round(documents * 100d, 1),
                OpportunityScore = Round(score, 1),
            };

            scored.Recommended = scored.OpportunityScore >= o.OpportunityRecommendScore;
            scored.Rationale = OpportunityRationale(scored);
            return scored;
        }

        /// <summary>
        /// A one-line, quotable justification for giving this person a seat. Written to be pasted
        /// straight into a licence request, so it leads with the strongest evidence available.
        /// </summary>
        public static string OpportunityRationale(LicenceOpportunityRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));

            var reasons = new List<string>();

            if (row.UnlicensedCopilotInteractions > 0)
            {
                reasons.Add(
                    $"already using Copilot Chat without a licence ({row.UnlicensedCopilotInteractions:N0} "
                    + $"interaction{(row.UnlicensedCopilotInteractions == 1 ? string.Empty : "s")} "
                    + $"across {row.UnlicensedCopilotActiveDays:N0} day{(row.UnlicensedCopilotActiveDays == 1 ? string.Empty : "s")})");
            }

            if (row.TeamsMessages + row.TeamsMeetings > 0)
            {
                reasons.Add($"{row.TeamsMessages:N0} Teams messages and {row.TeamsMeetings:N0} meetings");
            }

            if (row.EmailsSent + row.EmailsRead > 0)
            {
                reasons.Add($"{row.EmailsSent:N0} emails sent, {row.EmailsRead:N0} read");
            }

            if (row.FilesViewedOrEdited > 0)
            {
                reasons.Add($"{row.FilesViewedOrEdited:N0} files viewed or edited");
            }

            if (reasons.Count == 0)
            {
                return "No qualifying Microsoft 365 activity recorded in this period.";
            }

            return (row.Recommended ? "Recommended: " : "Candidate: ") + string.Join("; ", reasons) + ".";
        }

        /// <summary>
        /// The <see cref="ScoreOpportunity"/> formula as a SQL expression, so the database can rank a
        /// 200,000-user tenant and hand back only the strongest candidates. Pulling every unlicensed
        /// user into memory to score them in C# is not an option at that size.
        ///
        /// Generated from the same options instance the C# scorer is given, and every returned row is
        /// re-scored in C# before it is displayed or exported - so this expression decides <i>which</i>
        /// users come back, never what their published score is.
        /// </summary>
        /// <param name="options">Tuning; must be the same instance/values used for the C# scoring.</param>
        /// <param name="copilotColumn">SQL expression yielding the user's unlicensed Copilot interactions.</param>
        /// <param name="teamsColumn">SQL expression yielding Teams messages.</param>
        /// <param name="meetingsColumn">SQL expression yielding Teams meetings.</param>
        /// <param name="emailSentColumn">SQL expression yielding emails sent.</param>
        /// <param name="emailReadColumn">SQL expression yielding emails read.</param>
        /// <param name="filesColumn">SQL expression yielding files viewed or edited.</param>
        public static string BuildOpportunityScoreSql(
            CopilotAdoptionOptions options,
            string copilotColumn,
            string teamsColumn,
            string meetingsColumn,
            string emailSentColumn,
            string emailReadColumn,
            string filesColumn)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            // All column arguments are compile-time constants supplied by CopilotAdoptionSql, never
            // user input, so there is no injection surface here.
            return
                Component(copilotColumn, o.OpportunityCopilotTarget, o.OpportunityUnlicensedCopilotWeight)
                + " + " + Component($"({teamsColumn} + {meetingsColumn})", o.OpportunityCollaborationTarget, o.OpportunityCollaborationWeight)
                + " + " + Component($"({emailSentColumn} + {emailReadColumn})", o.OpportunityEmailTarget, o.OpportunityEmailWeight)
                + " + " + Component(filesColumn, o.OpportunityDocumentTarget, o.OpportunityDocumentWeight);
        }

        /// <summary>One weighted, capped component of the SQL opportunity score.</summary>
        private static string Component(string valueSql, double target, double weight)
        {
            var safeTarget = target <= 0 ? 1 : target;
            // CAST to float first so integer division never silently floors the ratio to 0 or 1.
            return $"(CASE WHEN CAST({valueSql} AS float) / {Num(safeTarget)} > 1 THEN 1.0 "
                 + $"ELSE CAST({valueSql} AS float) / {Num(safeTarget)} END) * {Num(weight)}";
        }

        #endregion

        #region Aggregation helpers

        /// <summary>
        /// Rolls a scored population up into one segment row (a department, a country, the whole
        /// tenant). Shared by the summary and by the per-segment charts so a department's adoption rate
        /// is computed identically wherever it appears.
        /// </summary>
        public static AdoptionSegmentRow Summarise(string segment, IEnumerable<LicensedUserAdoptionRow> users)
        {
            var list = users as IList<LicensedUserAdoptionRow> ?? users?.ToList() ?? new List<LicensedUserAdoptionRow>();

            var licensed = list.Count;
            var active = list.Count(u => u.Band > AdoptionBand.Dormant);
            var habitual = list.Count(u => IsHabitual(u.Band));
            var never = list.Count(u => u.Band == AdoptionBand.NeverUsed);

            return new AdoptionSegmentRow
            {
                Segment = segment,
                LicensedUsers = licensed,
                ActiveUsers = active,
                HabitualUsers = habitual,
                NeverUsedUsers = never,
                AdoptionRatePct = Percentage(active, licensed),
                AverageAdoptionScore = licensed == 0 ? 0 : Round(list.Average(u => u.AdoptionScore), 1),
            };
        }

        /// <summary><paramref name="part"/> as a percentage of <paramref name="total"/>, to one decimal place; 0 when there is no total.</summary>
        public static double Percentage(double part, double total)
        {
            if (total <= 0) return 0;
            return Round(part / total * 100d, 1);
        }

        /// <summary>Median of a sequence, to one decimal place. 0 for an empty sequence.</summary>
        public static double Median(IEnumerable<double> values)
        {
            var ordered = values?.OrderBy(v => v).ToList() ?? new List<double>();
            if (ordered.Count == 0) return 0;

            var mid = ordered.Count / 2;
            var median = ordered.Count % 2 == 1
                ? ordered[mid]
                : (ordered[mid - 1] + ordered[mid]) / 2d;
            return Round(median, 1);
        }

        #endregion

        #region Primitives

        /// <summary>A 0..1 ratio of value against target, guarding against a zero or negative target.</summary>
        private static double Ratio(double value, double target)
        {
            if (target <= 0) return value > 0 ? 1d : 0d;
            if (value <= 0) return 0d;
            var ratio = value / target;
            return ratio > 1d ? 1d : ratio;
        }

        private static double Round(double value, int decimals)
        {
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        /// <summary>Formats a double for embedding in SQL - invariant culture, so a comma decimal
        /// separator on a European server can never produce syntactically broken SQL.</summary>
        private static string Num(double value)
        {
            return value.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        private static string AppsPhrase(int appsUsed)
        {
            return appsUsed == 1 ? "a single Copilot app" : $"{appsUsed} Copilot apps";
        }

        #endregion
    }
}

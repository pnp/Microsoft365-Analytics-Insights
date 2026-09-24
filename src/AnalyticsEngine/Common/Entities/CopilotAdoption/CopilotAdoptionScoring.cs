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
    /// The exceptions are <see cref="BuildOpportunityScoreSql"/> and
    /// <see cref="BuildCoworkLoadScoreSql"/>, which emit SQL expressions for the same formulae so the
    /// database can rank hundreds of thousands of users and return only the rows worth scoring. Both are
    /// generated from the same <see cref="CopilotAdoptionOptions"/> instance, and the returned rows are
    /// always re-scored here before display, so the SQL is a ranking aid rather than a second source of
    /// truth.
    /// </summary>
    public static class CopilotAdoptionScoring
    {
        /// <summary>Score built from our own Copilot audit-log import.</summary>
        public const string SignalSourceAudit = "audit";

        /// <summary>Score built from Microsoft's per-user Copilot usage report.</summary>
        public const string SignalSourceUsageReport = "usageReport";

        /// <summary>The current tenure signal is Entra account age, not true Copilot seat tenure.</summary>
        public const string TenureBasisAccountAge = "accountAge";

        /// <summary>No tenure signal was available, so reclaim confidence is deliberately lowered.</summary>
        public const string TenureBasisUnknown = "unknown";

        public static class ReclaimEligibilityTiers
        {
            public const string Certain = "certain";
            public const string Probable = "probable";
            public const string Review = "review";
            public const string Excluded = "excluded";
        }

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
        /// Inclusive UTC start of a "last N days" window that ends today.
        ///
        /// Deliberately <c>N-1</c> days back, not <c>N</c>. Midnight N days ago spans <b>N+1</b> distinct
        /// calendar dates, so a user active every single day would record 29 active days in a "28-day"
        /// window - while the frequency component divides by a target derived from 28. The numerator and
        /// the denominator have to be measuring the same window, or the score is quietly inflated for
        /// exactly the most engaged users, and "last 28 days" in the UI means something else again.
        ///
        /// Lives next to <see cref="TargetActiveDays"/> on purpose: these two are the window definition,
        /// and they must move together.
        /// </summary>
        public static DateTime WindowStartUtc(DateTime nowUtc, int windowDays)
        {
            var days = Math.Max(1, windowDays);
            return nowUtc.Date.AddDays(-(days - 1));
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
        /// Frequency target adjusted for a new user's actual observation window. Until issue #277 adds
        /// real seat-tenure history, <c>users.created_utc</c> is the best cheap proxy we have. Only the
        /// early-tenure case is prorated; established users still use the full-window target so the
        /// metric remains comparable across the population.
        /// </summary>
        /// <remarks>
        /// Prorated against the REPORTING WINDOW, not against <see cref="CopilotAdoptionOptions.ReclaimGraceDays"/>.
        /// Those are different questions: the grace period decides whether an idle seat is too new to
        /// reclaim, while this decides how many active days it was even possible for the person to have.
        /// Tying the proration to the grace period put a cliff at exactly the grace boundary on any
        /// window longer than it - on a 180-day report a 29-day-old account was measured against about
        /// 13 active days and a 30-day-old one against 77, so somebody who had used Copilot every
        /// working day since joining was scored as a light user for having existed one day longer. At
        /// the default 28-day window the two rules coincide exactly, so nothing changes there.
        /// </remarks>
        public static double TargetActiveDaysForTenure(LicensedUserUsageRow row, DateTime nowUtc, CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var target = TargetActiveDays(o);
            var days = DaysSinceTenureStart(row?.AccountCreatedUtc, nowUtc);
            if (!days.HasValue) return target;

            var windowDays = Math.Max(1, o.WindowDays);
            // "days since creation" counts whole days, so an account created today has been available
            // for one day of the window, not zero.
            var observedDays = Math.Max(1, Math.Min(windowDays, days.Value + 1));
            if (observedDays >= windowDays) return target;

            return Math.Max(1d, target * observedDays / windowDays);
        }

        /// <summary>Whole days since the tenure proxy started, clamped to zero for clock skew.</summary>
        public static int? DaysSinceTenureStart(DateTime? tenureStartUtc, DateTime nowUtc)
        {
            if (!tenureStartUtc.HasValue) return null;
            return Math.Max(0, (int)(nowUtc.Date - tenureStartUtc.Value.Date).TotalDays);
        }

        /// <summary>Whether the only tenure signal says the user is still inside the grace period.</summary>
        public static bool IsTooNewToJudge(LicensedUserUsageRow row, DateTime nowUtc, CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var days = DaysSinceTenureStart(row?.AccountCreatedUtc, nowUtc);
            return days.HasValue && days.Value < Math.Max(1, o.ReclaimGraceDays);
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
            var reportCoversUser = row.ReportActiveDays.HasValue
                || row.ReportPrompts.HasValue
                || row.ReportLastActivityUtc.HasValue;
            var useReport = (!auditAvailable || !auditHasSignal) && reportHasSignal;

            var activeDays = useReport ? (row.ReportActiveDays ?? 0) : row.ActiveDays;
            var interactions = useReport ? (row.ReportPrompts ?? 0) : row.Interactions;
            var appsUsed = useReport ? (row.ReportAppsUsed ?? 0) : row.AppsUsed;
            var lastUse = useReport
                ? row.ReportLastActivityUtc
                : (row.LastInteractionUtc ?? row.ReportLastActivityUtc);

            var targetActiveDays = TargetActiveDaysForTenure(row, nowUtc, o);
            var frequency = Ratio(activeDays, targetActiveDays);
            // Depth divides by a number the user controls, so one active day would otherwise make full
            // marks trivial to reach - see CopilotAdoptionOptions.DepthMinActiveDays. Scaling by the
            // size of the sample stops a single afternoon's experiment reading as a forming habit,
            // and leaves anyone at or above the minimum completely unaffected.
            var depthConfidence = Ratio(activeDays, o.DepthMinActiveDays);
            var depth = activeDays > 0
                ? Ratio((double)interactions / activeDays, o.DepthTargetInteractionsPerActiveDay) * depthConfidence
                : 0d;
            var breadth = Ratio(appsUsed, o.BreadthTargetApps);

            var weightSum = o.FrequencyWeight + o.DepthWeight + o.BreadthWeight;
            var score = weightSum <= 0
                ? 0d
                : (frequency * o.FrequencyWeight + depth * o.DepthWeight + breadth * o.BreadthWeight)
                  / weightSum * 100d;

            // An in-window last-activity date IS use in the window, even when the counters are missing.
            // A v1-shaped or partial response carries lastActivityDate but no prompts/active-days, so
            // ReportPrompts and ReportActiveDays are NULL - and reportHasSignal coalesces those to 0, so
            // useReport stays false and both counters read 0. Without this clause the user banded
            // NeverUsed, and ApplyReclaimEligibility turns an enabled NeverUsed account past the grace
            // period into "Probable" - naming for licence removal someone Microsoft's own report says was
            // active days ago. lastUse already falls back to ReportLastActivityUtc, and the line below
            // uses the same value to decide "used before the window"; it just never asked the in-window
            // question.
            var usedInWindow = interactions > 0 || activeDays > 0
                || (lastUse.HasValue && lastUse.Value >= windowStartUtc);
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
                EmailDomain = CopilotAdoptionEmailDomain.From(row.UserPrincipalName, row.Mail),
                Department = row.Department,
                JobTitle = row.JobTitle,
                Country = row.Country,
                OfficeLocation = row.OfficeLocation,
                CompanyName = row.CompanyName,
                ManagerUserPrincipalName = row.ManagerUserPrincipalName,
                AccountEnabled = row.AccountEnabled,
                AccountCreatedUtc = row.AccountCreatedUtc,
                TenureStartUtc = row.AccountCreatedUtc,
                TenureBasis = row.AccountCreatedUtc.HasValue ? TenureBasisAccountAge : TenureBasisUnknown,
                DaysSinceTenureStart = DaysSinceTenureStart(row.AccountCreatedUtc, nowUtc),
                TooNewToJudge = IsTooNewToJudge(row, nowUtc, o),
                ReclaimExclusionReason = row.ReclaimExclusionReason,
                ReclaimExclusionNote = row.ReclaimExclusionNote,
                ReclaimExcludedBy = row.ReclaimExcludedBy,
                ReclaimExcludedUtc = row.ReclaimExcludedUtc,
                ReclaimExclusionReviewAfterUtc = row.ReclaimExclusionReviewAfterUtc,
                ReclaimExclusionExpired = row.ReclaimExclusionExpired,
                SeatLicences = row.SeatLicences,
                SeatLicenceTypeIds = row.SeatLicenceTypeIds,

                Interactions = interactions,
                ActiveDays = activeDays,
                AuditInteractions = row.Interactions,
                AuditActiveDays = row.ActiveDays,
                AuditAppsUsed = row.AppsUsed,
                SourceComparisonAvailable = auditAvailable && reportCoversUser,
                ExpectedActiveDays = Round(targetActiveDays, 1),
                AppsUsed = appsUsed,
                AgentsUsed = row.AgentsUsed,
                CoworkInteractions = row.CoworkInteractions,
                CoworkReportTotalTasks = row.CoworkReportTotalTasks,
                CoworkReportScheduledTasks = row.CoworkReportScheduledTasks,
                CoworkReportUserInitiatedTasks = row.CoworkReportUserInitiatedTasks,
                CoworkReportActiveDays = row.CoworkReportActiveDays,
                CoworkReportLastActivityDate = row.CoworkReportLastActivityDate,
                CoworkReportRetainedUser = row.CoworkReportRetainedUser,
                CoworkAutomationRatioPct = row.CoworkReportTotalTasks.GetValueOrDefault() > 0 && row.CoworkReportScheduledTasks.HasValue
                    ? (double?)Percentage(row.CoworkReportScheduledTasks.Value, row.CoworkReportTotalTasks.Value)
                    : null,
                // Report active days count as evidence too. Without them a user Microsoft reports as
                // active on N days but whose task count is blank would be tiered Established while
                // UsedCowork said "no" - contradicting the tier, the CSV column and the workbook.
                UsedCowork = row.CoworkReportTotalTasks.GetValueOrDefault() > 0
                    || row.CoworkReportActiveDays.GetValueOrDefault() > 0
                    || row.CoworkInteractions > 0,
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

            ApplyReclaimEligibility(scored, o);
            scored.RecommendedActionCode = RecommendedActionCode(scored);
            scored.RecommendedActionLabel = ActionLabel(scored.RecommendedActionCode);
            scored.RecommendedAction = RecommendedAction(scored, o);
            return scored;
        }

        /// <summary>
        /// Assigns the reclaim confidence tier from the same row fields the drill-through list exposes.
        /// Disabled seats are certain. Never-used enabled accounts with enough tenure are probable.
        /// Dormant, too-new and unknown-tenure users are review-only because leave, part-time work and
        /// service/shared accounts are not observable from Microsoft 365 activity data.
        /// </summary>
        public static void ApplyReclaimEligibility(LicensedUserAdoptionRow row, CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            if (!string.IsNullOrEmpty(row.ReclaimExclusionReason))
            {
                row.ReclaimEligibility = ReclaimEligibilityTiers.Excluded;
                row.ReclaimEligibilityReason = $"Excluded by admin: {row.ReclaimExclusionReason}.";
                return;
            }

            if (row.AccountEnabled == false)
            {
                row.ReclaimEligibility = ReclaimEligibilityTiers.Certain;
                row.ReclaimEligibilityReason = "Disabled account still holds a Copilot seat. Reclaim immediately.";
                return;
            }

            if (row.Band == AdoptionBand.NeverUsed)
            {
                if (row.TooNewToJudge)
                {
                    row.ReclaimEligibility = ReclaimEligibilityTiers.Review;
                    row.ReclaimEligibilityReason = $"Too new to judge: {row.TenureBasis} is below the {o.ReclaimGraceDays}-day grace period.";
                }
                else if (row.AccountEnabled == true && row.DaysSinceTenureStart.HasValue)
                {
                    row.ReclaimEligibility = ReclaimEligibilityTiers.Probable;
                    row.ReclaimEligibilityReason = $"No observed Copilot use and {row.TenureBasis} is beyond the {o.ReclaimGraceDays}-day grace period.";
                }
                else
                {
                    row.ReclaimEligibility = ReclaimEligibilityTiers.Review;
                    row.ReclaimEligibilityReason = "No observed Copilot use, but account state or tenure is unknown.";
                }
                return;
            }

            if (row.Band == AdoptionBand.Dormant)
            {
                row.ReclaimEligibility = ReclaimEligibilityTiers.Review;
                row.ReclaimEligibilityReason = "Dormant seat: review with the user's manager before reclaiming.";
            }
            else
            {
                row.ReclaimEligibility = string.Empty;
                row.ReclaimEligibilityReason = string.Empty;
            }
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
        /// Whether this user did anything with Copilot in the window.
        ///
        /// One predicate, used by every headcount. Scoring treats interactions OR active days as
        /// activity, because Microsoft's usage report can supply a prompt count with no per-day
        /// breakdown - so testing active days alone silently dropped those users from the
        /// concentration, department and intensity views while the headline still counted them.
        /// </summary>
        public static bool IsActive(LicensedUserAdoptionRow row)
        {
            if (row == null) return false;
            return row.Interactions > 0 || row.ActiveDays > 0;
        }

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

            // Precedence deliberately matches ApplyReclaimEligibility, so the tier, the action code and
            // this sentence can never disagree: an admin exclusion outranks everything, then a disabled
            // account, then the review cases.
            if (row.ReclaimEligibility == ReclaimEligibilityTiers.Excluded)
            {
                var review = row.ReclaimExclusionReviewAfterUtc.HasValue
                    ? $" Review after {row.ReclaimExclusionReviewAfterUtc.Value:yyyy-MM-dd}."
                    : " Permanent until an admin changes it.";
                return $"Excluded from reclaim - {row.ReclaimExclusionReason}.{review}";
            }

            // A disabled account still holding a seat is a reclaim regardless of how it was used before
            // it was disabled - and regardless of how new it is. Checked ahead of the too-new branch
            // below, which would otherwise tell an admin not to reclaim a seat the tier calls certain.
            if (row.AccountEnabled == false)
            {
                return "Reclaim - the account is disabled but still holds a Copilot licence. "
                     + "This is the clearest reclaim there is; no enablement effort is worthwhile.";
            }

            if (row.TooNewToJudge && row.Band == AdoptionBand.NeverUsed)
            {
                return $"Review before reclaim - Too new to judge: {row.TenureBasis} is below the {o.ReclaimGraceDays}-day grace period. Do not reclaim yet; check again after onboarding has had time to work.";
            }

            if (row.ReclaimEligibility == ReclaimEligibilityTiers.Review && row.Band == AdoptionBand.NeverUsed)
            {
                return "Review before reclaim - no observed Copilot use, but the account state or tenure signal is incomplete. Confirm this is not leave, part-time work, a service account or a shared mailbox before reassigning the licence.";
            }

            switch (row.Band)
            {
                case AdoptionBand.NeverUsed:
                    return $"Reclaim or onboard - no Copilot activity in the last {o.HistoryDays} days. "
                         + "Confirm the licence is still needed before renewal.";

                case AdoptionBand.Dormant:
                    var since = row.DaysSinceLastUse.HasValue
                        ? $"last used it {row.DaysSinceLastUse.Value} days ago"
                        : "has used it in the past";
                    // The action is enablement ("win back"), but the seat decision for a dormant row is
                    // review-only - there is still somebody to talk to. Saying "or reassign the licence"
                    // without that caveat reads as permission to reclaim, which contradicts the tier the
                    // same row carries.
                    return $"Win back - {since} but not once in this period. "
                         + "Ask what stopped and offer a refresher. This seat is review-only for reclaim: "
                         + "check with the user or their manager before reassigning it, because leave, "
                         + "part-time patterns and role changes are not visible in usage data.";

                case AdoptionBand.Trialling:
                    return "Build a first habit - occasional use only. Target one repeatable Copilot habit in "
                         + "the app they already live in.";

                case AdoptionBand.Developing:
                    return row.BreadthScore < 34
                        ? $"Add a second app - building a habit but only in {AppsPhrase(row.AppsUsed)}. Introduce "
                          + "a second surface such as Outlook or Teams meeting recaps."
                        : "Deepen to daily use - a habit is forming. A short scenario-based session should move "
                          + "them to daily use.";

                case AdoptionBand.Established:
                    return row.BreadthScore < 50
                        ? $"Add a second app - solid regular use, but confined to {AppsPhrase(row.AppsUsed)}; "
                          + "showing them one more surface is the cheapest remaining gain."
                        : "No action needed - Copilot is part of their working week.";

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
            public const string Review = "review";
            public const string Excluded = "excluded";
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
            AdoptionActionCodes.Review,
            AdoptionActionCodes.Excluded,
        };

        /// <summary>
        /// Which action this user needs, as a code. Shares its branching with
        /// <see cref="RecommendedAction"/> so the tag on screen can never disagree with the sentence in
        /// the CSV.
        /// </summary>
        public static string RecommendedActionCode(LicensedUserAdoptionRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));

            // Same precedence as ApplyReclaimEligibility and RecommendedAction: an admin exclusion
            // outranks everything, then a disabled account, then the review cases.
            if (row.ReclaimEligibility == ReclaimEligibilityTiers.Excluded) return AdoptionActionCodes.Excluded;

            // A disabled account holding a Copilot seat is the clearest reclaim there is, whatever its
            // usage looked like while it was still in use. Branching on the band alone told a disabled
            // account that had been active to "win back" - i.e. write to someone who has left - and a
            // recently active one that no action was needed.
            if (row.AccountEnabled == false) return AdoptionActionCodes.Reclaim;

            if (row.ReclaimEligibility == ReclaimEligibilityTiers.Review && row.Band == AdoptionBand.NeverUsed) return AdoptionActionCodes.Review;

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

        /// <summary>
        /// Short display label for an action code.
        ///
        /// Each label names the step to take, not the state the user is in. "Coach" and "Grow" were
        /// the original labels and read as synonyms to anyone who had not memorised the band
        /// definitions, which defeats the point of an action column: two rows that need genuinely
        /// different interventions looked like the same instruction.
        /// </summary>
        public static string ActionLabel(string code)
        {
            switch (code)
            {
                case AdoptionActionCodes.Reclaim: return "Reclaim or onboard";
                case AdoptionActionCodes.Reengage: return "Win back";
                case AdoptionActionCodes.Coach: return "Build a first habit";
                case AdoptionActionCodes.Broaden: return "Add a second app";
                case AdoptionActionCodes.Grow: return "Deepen to daily use";
                case AdoptionActionCodes.Sustain: return "No action needed";
                case AdoptionActionCodes.Advocate: return "Recruit as advocate";
                case AdoptionActionCodes.Review: return "Review before reclaim";
                case AdoptionActionCodes.Excluded: return "Excluded from reclaim";
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
                    return "Two routes reach this group, and both are safe to act on. A disabled account "
                         + "still holding a seat is the clearest reclaim there is whatever its usage looked "
                         + "like before it was disabled - no enablement effort is worthwhile on an account "
                         + $"nobody can sign into. The rest had no Copilot activity anywhere in the last {o.HistoryDays} "
                         + "days - the whole history this analysis reads - and have enough account tenure to judge: "
                         + "confirm the licence is still needed before renewal. New starters inside the grace period "
                         + "and users with no tenure evidence are NOT here; they are under 'Review before reclaim'.";

                case AdoptionActionCodes.Reengage:
                    return "Used Copilot before this period but not once inside it. Someone who tried it and "
                         + "stopped is a different problem from someone who never started - ask what stopped "
                         + "and offer a refresher. These seats carry the review-only reclaim tier, so confirm "
                         + "with the user or their manager before reassigning one: leave, part-time patterns "
                         + "and role changes are not visible in usage data.";

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
                         + "part of their working week. No action needed; these are the licences that are paying "
                         + "for themselves.";

                case AdoptionActionCodes.Advocate:
                    return $"Engagement at or above {o.ChampionScore} - among your deepest users. Ask them to "
                         + "run a peer session for their own department, which converts better than centrally "
                         + "run training. If their breadth score is low they are still worth showing one more "
                         + "surface.";

                case AdoptionActionCodes.Review:
                    return "Potential reclaim cases that are too new to judge, or missing enough tenure or "
                         + "account-state context to act on automatically. Leave, part-time patterns, service "
                         + "accounts and shared mailboxes are not detectable from usage data, so a human review "
                         + "is required. Dormant seats are deliberately NOT here - they get the Win back action, "
                         + "because there is still somebody to talk to - but they are counted as review-only in "
                         + "the reclaim tiers, which is a seat decision rather than an enablement one.";

                case AdoptionActionCodes.Excluded:
                    return "Reviewed cases an admin deliberately excluded from reclaim. They still hold a seat "
                         + "and remain in the licence denominator, but do not inflate the actionable reclaim KPI.";

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
                WindowInteractions = row.WindowInteractions,
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

                // GetRange rather than Skip().Take(): Skip walks the list from the start every time,
                // which at 200k active users would re-traverse the collection once per cohort for no
                // reason. GetRange copies the slice directly.
                var slice = ranked.GetRange(taken, size);
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

            var copilot = Ratio(row.UnlicensedCopilotInteractions, OpportunityCopilotTargetForWindow(o));
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
                EmailDomain = CopilotAdoptionEmailDomain.From(row.UserPrincipalName, row.Mail),
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

            // Proven demand qualifies on its own. The composite score cannot express this: the Copilot
            // weight (35) sits below the recommendation bar (50), so recurrent unlicensed use could
            // never clear it unaided while general busyness (65) could. See
            // CopilotAdoptionOptions.OpportunityProvenDemandMinActiveDays.
            var provenDemand = row.UnlicensedCopilotActiveDays >= Math.Max(1, o.OpportunityProvenDemandMinActiveDays);

            scored.Recommended = provenDemand || scored.OpportunityScore >= o.OpportunityRecommendScore;
            scored.QualificationTier = provenDemand
                ? OpportunityTiers.ProvenDemand
                : scored.Recommended ? OpportunityTiers.WorkloadInferred : OpportunityTiers.None;
            scored.QualificationTierLabel = OpportunityTierLabel(scored.QualificationTier);
            scored.Rationale = OpportunityRationale(scored);
            return scored;
        }

        /// <summary>
        /// <see cref="CopilotAdoptionOptions.OpportunityCopilotTarget"/> scaled from its basis period to
        /// the reporting window actually being analysed.
        ///
        /// The other three opportunity components are per-active-day averages, so they already mean the
        /// same thing at any window length. This one is a raw total, and without scaling it silently
        /// changes meaning with the period drop-down: 20 interactions is heavy use over a week and
        /// almost nothing over six months, so the same person would be recommended for a licence at one
        /// setting and not at another. Never below 1, so a short window cannot make the target free.
        /// </summary>
        public static double OpportunityCopilotTargetForWindow(CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var basisDays = Math.Max(1, o.OpportunityCopilotTargetBasisDays);
            var windowDays = Math.Max(1, o.WindowDays);
            return Math.Max(1d, o.OpportunityCopilotTarget * windowDays / basisDays);
        }

        /// <summary>
        /// Why an unlicensed user qualifies for a seat. Split from the score because the two routes
        /// justify a purchase very differently: one is evidence, the other is inference.
        /// </summary>
        public static class OpportunityTiers
        {
            /// <summary>Recurrent unlicensed Copilot use - the person is already doing it.</summary>
            public const string ProvenDemand = "provenDemand";

            /// <summary>No Copilot use, but a workload pattern that suggests they would benefit.</summary>
            public const string WorkloadInferred = "workloadInferred";

            /// <summary>Below the bar on both routes.</summary>
            public const string None = "none";
        }

        /// <summary>Short display label for an opportunity tier.</summary>
        public static string OpportunityTierLabel(string tier)
        {
            switch (tier)
            {
                case OpportunityTiers.ProvenDemand: return "Proven demand";
                case OpportunityTiers.WorkloadInferred: return "Candidate for assessment";
                case OpportunityTiers.None: return "Not recommended";
                default: return string.Empty;
            }
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

            // Name the route as well as the evidence. "Recommended" on its own does not say whether the
            // person is already using Copilot or merely looks like someone who would.
            string prefix;
            switch (row.QualificationTier)
            {
                case OpportunityTiers.ProvenDemand:
                    prefix = "Recommended - proven demand: ";
                    break;
                case OpportunityTiers.WorkloadInferred:
                    prefix = "Candidate for assessment: ";
                    break;
                default:
                    prefix = "Not recommended: ";
                    break;
            }

            return prefix + string.Join("; ", reasons) + ".";
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
                Component(copilotColumn, OpportunityCopilotTargetForWindow(o), o.OpportunityUnlicensedCopilotWeight)
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

        #region Cowork readiness

        /// <summary>
        /// Scores a Copilot seat holder for Microsoft 365 Copilot Cowork readiness.
        ///
        /// <para>Cowork is an agentic delegation layer: you describe an outcome and it plans and runs
        /// multi-step work across Outlook, Teams, the Office apps and SharePoint/OneDrive. That shapes the
        /// whole model. Two things have to be true before enabling it for someone is a good idea:</para>
        ///
        /// <list type="number">
        /// <item><b>They have delegable work</b> - a real coordination load of meetings, mail and document
        /// churn. Without it Cowork has nothing to absorb and simply burns credits.</item>
        /// <item><b>They can delegate it</b> - enough existing Copilot fluency to trust an agent with a
        /// multi-step task. Cowork is a step up from Copilot, not an entry point.</item>
        /// </list>
        ///
        /// <para>Neither signal is sufficient alone, which is why this produces two independent axes rather
        /// than one blended number. A blend would average a heavy-workload novice and a fluent user with no
        /// coordination work into the same middling score, and those two need opposite interventions -
        /// exactly the failure <see cref="AdoptionScoreProfile"/> exists to avoid on the main report.</para>
        /// </summary>
        public static CoworkReadinessRow ScoreCoworkReadiness(
            CoworkReadinessSignalRow row,
            CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            var collaboration = Ratio(row.TeamsMessages, o.CoworkCollaborationTarget);
            var meetings = Ratio(row.TeamsMeetings, o.CoworkMeetingTarget);
            var email = Ratio(row.EmailsSent + row.EmailsRead, o.CoworkEmailTarget);
            var documents = Ratio(row.FilesViewedOrEdited, o.CoworkDocumentTarget);

            var load = collaboration * o.CoworkCollaborationWeight
                     + meetings * o.CoworkMeetingWeight
                     + email * o.CoworkEmailWeight
                     + documents * o.CoworkDocumentWeight;

            // Agent familiarity is evidence of the delegation habit itself, which the engagement score does
            // not capture - but it is an uplift, not a substitute. Clamped to 100 so it can promote a
            // borderline user without ever carrying an inactive one over the fluency bar.
            var uplift = row.AgentsUsed > 0 ? Math.Max(0d, o.CoworkAgentFamiliarityUplift) : 0d;
            var fluency = Math.Min(100d, Math.Max(0d, row.AdoptionScore) + uplift);

            var scored = new CoworkReadinessRow
            {
                UserId = row.UserId,
                UserPrincipalName = row.UserPrincipalName,
                Mail = row.Mail,
                EmailDomain = row.EmailDomain ?? CopilotAdoptionEmailDomain.From(row.UserPrincipalName, row.Mail),
                Department = row.Department,
                JobTitle = row.JobTitle,
                Country = row.Country,
                OfficeLocation = row.OfficeLocation,
                CompanyName = row.CompanyName,
                ManagerUserPrincipalName = row.ManagerUserPrincipalName,
                AccountEnabled = row.AccountEnabled,

                CoworkInteractions = row.CoworkInteractions,
                CoworkActiveDays = row.CoworkActiveDays,
                LastCoworkInteractionUtc = row.LastCoworkInteractionUtc,
                CoworkReportTotalTasks = row.CoworkReportTotalTasks,
                CoworkReportScheduledTasks = row.CoworkReportScheduledTasks,
                CoworkReportUserInitiatedTasks = row.CoworkReportUserInitiatedTasks,
                CoworkReportActiveDays = row.CoworkReportActiveDays,
                CoworkReportLastActivityDate = row.CoworkReportLastActivityDate,
                CoworkReportRetainedUser = row.CoworkReportRetainedUser,
                CoworkAutomationRatioPct = row.CoworkReportTotalTasks.GetValueOrDefault() > 0 && row.CoworkReportScheduledTasks.HasValue
                    ? (double?)Percentage(row.CoworkReportScheduledTasks.Value, row.CoworkReportTotalTasks.Value)
                    : null,
                // Report active days count as evidence too. Without them a user Microsoft reports as
                // active on N days but whose task count is blank would be tiered Established while
                // UsedCowork said "no" - contradicting the tier, the CSV column and the workbook.
                UsedCowork = row.CoworkReportTotalTasks.GetValueOrDefault() > 0
                    || row.CoworkReportActiveDays.GetValueOrDefault() > 0
                    || row.CoworkInteractions > 0,

                TeamsMessages = row.TeamsMessages,
                TeamsMeetings = row.TeamsMeetings,
                EmailsSent = row.EmailsSent,
                EmailsRead = row.EmailsRead,
                FilesViewedOrEdited = row.FilesViewedOrEdited,
                LastM365ActivityUtc = row.LastM365ActivityUtc,

                MeetingsOrganisedPerActiveDay = row.MeetingsOrganisedPerActiveDay,
                MeetingsAttendedPerActiveDay = row.MeetingsAttendedPerActiveDay,
                ChatAndChannelMessagesPerActiveDay = row.ChatAndChannelMessagesPerActiveDay,
                EmailsSentPerActiveDay = row.EmailsSentPerActiveDay,
                FilesPerActiveDay = row.FilesPerActiveDay,

                CollaborationScore = Round(collaboration * 100d, 1),
                MeetingScore = Round(meetings * 100d, 1),
                EmailScore = Round(email * 100d, 1),
                DocumentScore = Round(documents * 100d, 1),

                CoordinationLoadScore = Round(load, 1),
                FluencyScore = Round(fluency, 1),
                AdoptionScore = Round(Math.Max(0d, row.AdoptionScore), 1),
                AgentsUsed = row.AgentsUsed,

                TotalCopilotCredits = row.TotalCopilotCredits,
            };

            if (scored.CoworkReportTotalTasks.GetValueOrDefault() > 0 && scored.TotalCopilotCredits.HasValue)
            {
                scored.CoworkCreditsPerTask = Math.Round(scored.TotalCopilotCredits.Value / scored.CoworkReportTotalTasks.Value, 4, MidpointRounding.AwayFromZero);
            }

            scored.RegularCoworkUser =
                (row.CoworkReportActiveDays ?? row.CoworkActiveDays) >= Math.Max(1, o.CoworkRegularMinActiveDays);

            scored.Tier = CoworkTierFor(scored, o);
            scored.TierLabel = CoworkTierLabel(scored.Tier);
            scored.Basis = CoworkTierBasis(scored.Tier);

            // Current users are in scope as well as prime candidates. Scoping a policy from candidates
            // alone would silently REMOVE access from the people already using Cowork - turning a rollout
            // list into an outage for exactly the users who proved the capability works.
            //
            // A DISABLED account is excluded regardless of tier. It can legitimately reach the evidence
            // tiers - someone who used Cowork right up to the day they were disabled is Established - but
            // the same row is simultaneously ReclaimEligibility = Certain ("Reclaim immediately", see
            // ScoreReclaimEligibility), so recommending it would put "grant this person Cowork" and
            // "take this person's licence away" on the same person in the same report. The account is
            // still SHOWN, deliberately: hiding it would stop the tab reconciling with the reclaim
            // figures. It is only not RECOMMENDED, because a spending policy scoped to a disabled
            // account grants nothing and just inflates the list an admin has to work through.
            //
            // Nullable-safe on purpose: == false is "known disabled". A NULL AccountEnabled means the
            // directory import did not state it, which is not evidence of being disabled, so those rows
            // keep their tier-derived recommendation - the same rule the reclaim scoring applies.
            scored.RecommendForPolicy =
                scored.AccountEnabled != false
                && (scored.Tier == CoworkTiers.Established
                    || scored.Tier == CoworkTiers.Trialling
                    || scored.Tier == CoworkTiers.PrimeCandidate);

            scored.Rationale = CoworkRationale(scored, o);
            return scored;
        }

        /// <summary>
        /// The Cowork populations. Ordered from strongest evidence to weakest, and evaluated in that
        /// order so every user lands in exactly one.
        /// </summary>
        public static class CoworkTiers
        {
            /// <summary>Regular, habitual Cowork use. Evidence.</summary>
            public const string Established = "established";

            /// <summary>Has used Cowork, but not yet regularly. Evidence.</summary>
            public const string Trialling = "trialling";

            /// <summary>Fluent with Copilot and carrying a real coordination load. The rollout target.</summary>
            public const string PrimeCandidate = "primeCandidate";

            /// <summary>Has the workload for Cowork but not yet the Copilot habit to delegate it.</summary>
            public const string BuildFluencyFirst = "buildFluencyFirst";

            /// <summary>Fluent with Copilot, but little delegable coordination work.</summary>
            public const string LowCoordinationLoad = "lowCoordinationLoad";

            /// <summary>Below the bar on both axes.</summary>
            public const string NotIndicated = "notIndicated";
        }

        /// <summary>Whether a tier rests on observed Cowork use or on inference.</summary>
        public static class CoworkBasis
        {
            /// <summary>The user has actually used Cowork - this is observed, not inferred.</summary>
            public const string Evidence = "evidence";

            /// <summary>Derived from workload and Copilot fluency. A prediction, and must read as one.</summary>
            public const string Inference = "inference";
        }

        /// <summary>Every Cowork tier, in report order. Used to build the tier breakdown and filters.</summary>
        public static IReadOnlyList<string> AllCoworkTiers { get; } = new[]
        {
            CoworkTiers.Established,
            CoworkTiers.Trialling,
            CoworkTiers.PrimeCandidate,
            CoworkTiers.BuildFluencyFirst,
            CoworkTiers.LowCoordinationLoad,
            CoworkTiers.NotIndicated,
        };

        /// <summary>
        /// Places a scored user in exactly one Cowork tier.
        ///
        /// Observed Cowork use is tested first and wins outright: someone already using it is not a
        /// "candidate" whatever their scores say, and demoting a real user to a predicted one because
        /// their measured workload looks light would be the report contradicting its own evidence.
        /// </summary>
        public static string CoworkTierFor(CoworkReadinessRow row, CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            // Microsoft's first-party Cowork report is the documented basis for this tab (#558); the audit
            // signal is only the fallback when the report has nothing for this user. Reading the audit
            // count alone here contradicted both RegularCoworkUser and the rationale text, which already
            // prefer the report - a report-only user with 10 active days was tiered "Trialling" and then
            // told "on 10 active days ... short of the 3 needed to count as regular use".
            var coworkActiveDays = row.CoworkReportActiveDays ?? row.CoworkActiveDays;

            if (coworkActiveDays >= Math.Max(1, o.CoworkRegularMinActiveDays))
            {
                return CoworkTiers.Established;
            }

            if (row.UsedCowork)
            {
                return CoworkTiers.Trialling;
            }

            var fluent = row.FluencyScore >= o.CoworkFluencyMinScore;
            var loaded = row.CoordinationLoadScore >= o.CoworkLoadMinScore;

            if (fluent && loaded) return CoworkTiers.PrimeCandidate;
            if (loaded) return CoworkTiers.BuildFluencyFirst;
            if (fluent) return CoworkTiers.LowCoordinationLoad;
            return CoworkTiers.NotIndicated;
        }

        /// <summary>Short display label for a Cowork tier.</summary>
        public static string CoworkTierLabel(string tier)
        {
            switch (tier)
            {
                case CoworkTiers.Established: return "Established";
                case CoworkTiers.Trialling: return "Trialling";
                case CoworkTiers.PrimeCandidate: return "Prime candidate";
                case CoworkTiers.BuildFluencyFirst: return "Build fluency first";
                case CoworkTiers.LowCoordinationLoad: return "Low coordination load";
                case CoworkTiers.NotIndicated: return "Not indicated";
                default: return string.Empty;
            }
        }

        /// <summary>Whether a tier is founded on observed Cowork use or on inference.</summary>
        public static string CoworkTierBasis(string tier)
        {
            switch (tier)
            {
                case CoworkTiers.Established:
                case CoworkTiers.Trialling:
                    return CoworkBasis.Evidence;
                default:
                    return CoworkBasis.Inference;
            }
        }

        /// <summary>
        /// True when Microsoft's first-party Cowork report carries any usage signal for this row. Task
        /// count and active days are independently nullable, so presence must not be inferred from the
        /// task cell alone - that proxy broke as soon as tiering started honouring active days.
        /// </summary>
        private static bool CoworkReportHasSignal(CoworkReadinessRow row)
        {
            return row.CoworkReportTotalTasks.GetValueOrDefault() > 0
                || row.CoworkReportActiveDays.GetValueOrDefault() > 0;
        }

        /// <summary>
        /// The evidence clause of a report-sourced rationale. When the report gave active days but no task
        /// count, the task clause is dropped entirely rather than printing a fabricated "0 Cowork tasks" -
        /// and the sentence still reads grammatically instead of "Already established: across 12 days".
        /// </summary>
        private static string ReportEvidencePhrase(CoworkReadinessRow row)
        {
            var days = row.CoworkReportActiveDays ?? 0;
            if (row.CoworkReportTotalTasks.HasValue)
            {
                return $"{row.CoworkReportTotalTasks.Value:N0} Cowork task"
                     + $"{Plural(row.CoworkReportTotalTasks.Value)} across {days:N0} active day{Plural(days)}";
            }

            return $"active on {days:N0} day{Plural(days)}";
        }

        /// <summary>What a Cowork tier means and what to do about it. Stated once per group.</summary>
        public static string CoworkTierDescription(string tier, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var days = Math.Max(1, o.CoworkRegularMinActiveDays);

            switch (tier)
            {
                case CoworkTiers.Established:
                    return $"Using Cowork on at least {days} separate days in this period. These people have "
                         + "already made it part of how they work - keep them in scope, and use them as the "
                         + "reference for what good looks like.";

                case CoworkTiers.Trialling:
                    return "Has used Cowork, but not yet regularly enough to call it a habit. Usually a "
                         + "prompt problem rather than a fit problem: they need a worked example on a task "
                         + "they actually own. Keep them in scope.";

                case CoworkTiers.PrimeCandidate:
                    return "Not yet using Cowork, but fluent with Copilot and carrying a heavy coordination "
                         + "load - the combination Cowork is built for. This is the rollout target: enable "
                         + "these people first.";

                case CoworkTiers.BuildFluencyFirst:
                    return "Has the workload Cowork would help with, but has not yet formed a Copilot habit. "
                         + "Enabling Cowork now would spend credits on someone unlikely to delegate to it. "
                         + "Bring them up on everyday Copilot first, then revisit.";

                case CoworkTiers.LowCoordinationLoad:
                    return "Comfortable with Copilot, but little of the multi-step coordination work Cowork "
                         + "takes on. Not a bad user - just not where this capability pays back. Revisit if "
                         + "their role changes.";

                case CoworkTiers.NotIndicated:
                    return "Neither a Copilot habit nor a heavy coordination load in this period. No case "
                         + "for Cowork on current evidence.";

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// A one-line, quotable justification for this user's tier, written to be pasted into a rollout
        /// request or a spending-policy change.
        ///
        /// Leads with observed Cowork use where it exists, and otherwise states plainly that the verdict is
        /// a prediction. The phrasing matters: this text ends up in a CSV that someone forwards to a
        /// budget holder, and "predicted" must not quietly become "measured" on the way.
        /// </summary>
        public static string CoworkRationale(CoworkReadinessRow row, CopilotAdoptionOptions options = null)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var o = options ?? CopilotAdoptionOptions.Default;

            var workload = new List<string>();

            if (row.TeamsMeetings > 0)
            {
                workload.Add($"{row.TeamsMeetings:N0} meeting{Plural(row.TeamsMeetings)} a day");
            }

            if (row.TeamsMessages > 0)
            {
                workload.Add($"{row.TeamsMessages:N0} Teams message{Plural(row.TeamsMessages)} a day");
            }

            var mail = row.EmailsSent + row.EmailsRead;
            if (mail > 0)
            {
                workload.Add($"{mail:N0} email{Plural(mail)} a day");
            }

            if (row.FilesViewedOrEdited > 0)
            {
                workload.Add($"{row.FilesViewedOrEdited:N0} file{Plural(row.FilesViewedOrEdited)} a day");
            }

            var workloadPhrase = workload.Count > 0
                ? string.Join(", ", workload)
                : "no recorded Microsoft 365 activity in this period";

            switch (row.Tier)
            {
                case CoworkTiers.Established:
                    if (CoworkReportHasSignal(row))
                    {
                        return $"Already established: {ReportEvidencePhrase(row)} in Microsoft's Cowork "
                             + "usage report. Keep in scope.";
                    }
                    return $"Already established by audit reconciliation: {row.CoworkInteractions:N0} Cowork interaction"
                         + $"{Plural(row.CoworkInteractions)} across {row.CoworkActiveDays:N0} day"
                         + $"{Plural(row.CoworkActiveDays)}. Keep in scope.";

                case CoworkTiers.Trialling:
                    if (CoworkReportHasSignal(row))
                    {
                        return $"Trialling: {ReportEvidencePhrase(row)} in Microsoft's Cowork usage report, "
                             + $"short of the {Math.Max(1, o.CoworkRegularMinActiveDays)} "
                             + "needed to count as regular use. Keep in scope and follow up.";
                    }
                    return $"Trialling by audit reconciliation: {row.CoworkInteractions:N0} Cowork interaction"
                         + $"{Plural(row.CoworkInteractions)} on {row.CoworkActiveDays:N0} day"
                         + $"{Plural(row.CoworkActiveDays)}, short of the {Math.Max(1, o.CoworkRegularMinActiveDays)} "
                         + "needed to count as regular use. Keep in scope and follow up.";

                case CoworkTiers.PrimeCandidate:
                    return $"Predicted fit - not yet measured: Copilot fluency {Num(row.FluencyScore)}/100 "
                         + $"and a coordination load of {Num(row.CoordinationLoadScore)}/100 ({workloadPhrase}). "
                         + "Recommended for the Cowork spending policy.";

                case CoworkTiers.BuildFluencyFirst:
                    return $"Predicted fit for the work ({workloadPhrase}), but Copilot fluency is only "
                         + $"{Num(row.FluencyScore)}/100 against a bar of {Num(o.CoworkFluencyMinScore)}. "
                         + "Build everyday Copilot use first.";

                case CoworkTiers.LowCoordinationLoad:
                    return $"Fluent with Copilot ({Num(row.FluencyScore)}/100) but a coordination load of "
                         + $"only {Num(row.CoordinationLoadScore)}/100 ({workloadPhrase}). Little for Cowork "
                         + "to take on.";

                default:
                    return $"No case on current evidence: Copilot fluency {Num(row.FluencyScore)}/100, "
                         + $"coordination load {Num(row.CoordinationLoadScore)}/100 ({workloadPhrase}).";
            }
        }

        /// <summary>
        /// The coordination-load formula as a SQL expression, so the database can rank a 200,000-user
        /// tenant and return only the rows worth scoring.
        ///
        /// Generated from the same options instance the C# scorer is given, on the same contract as
        /// <see cref="BuildOpportunityScoreSql"/>: this decides <i>which</i> rows come back, never what
        /// their published score is. Every returned row is re-scored in C# before it is displayed.
        /// </summary>
        public static string BuildCoworkLoadScoreSql(
            CopilotAdoptionOptions options,
            string teamsColumn,
            string meetingsColumn,
            string emailSentColumn,
            string emailReadColumn,
            string filesColumn)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            // All column arguments are compile-time constants supplied by CopilotAdoptionSql, never user
            // input, so there is no injection surface here.
            return
                Component(teamsColumn, o.CoworkCollaborationTarget, o.CoworkCollaborationWeight)
                + " + " + Component(meetingsColumn, o.CoworkMeetingTarget, o.CoworkMeetingWeight)
                + " + " + Component($"({emailSentColumn} + {emailReadColumn})", o.CoworkEmailTarget, o.CoworkEmailWeight)
                + " + " + Component(filesColumn, o.CoworkDocumentTarget, o.CoworkDocumentWeight);
        }

        /// <summary>
        /// Working days in the report's month: the multiplier that restates a per-active-day volume as a
        /// monthly one. Both time-saved estimates quote "a month" on this basis, and the portal divides by
        /// the same figure to quote them per person per working day.
        /// </summary>
        public static double WorkingDaysPerMonth(CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            return Math.Max(1d, o.HabitBucketNormalisationDays * (o.WorkingDaysPerWeek / 7d));
        }

        /// <summary>
        /// The share of every minutes-saved assumption the conservative end of a time-saved range applies,
        /// clamped to 0..1: above 1 the "low" end would exceed the "high" one and print a backwards range,
        /// and below 0 it would invent a saving out of nothing. Shared by both estimates.
        /// </summary>
        public static double TimeSavedLowerBoundRatio(CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            return Math.Min(1d, NonNegative(o.CoworkEstimateLowerBoundRatio));
        }

        /// <summary>
        /// The multiplier that restates a Cowork task count over Microsoft's report period as tasks in the
        /// same month the rest of the model uses; zero when the period is unknown.
        ///
        /// <para>Zero rather than a guess on purpose. A snapshot imported before the period was recorded
        /// has tasks over an unknown number of days, and treating 180 days of tasks as one month would
        /// multiply the Cowork layer six-fold. Those tasks are left out of the observed side and the
        /// people are projected instead, which is labelled.</para>
        /// </summary>
        public static double CoworkTasksPerMonthFactor(int reportPeriodDays, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            if (reportPeriodDays <= 0) return 0d;
            return Math.Max(1, o.HabitBucketNormalisationDays) / (double)reportPeriodDays;
        }

        /// <summary>
        /// The Cowork tasks a month the tenant's own Cowork users run on average: everyone with tasks in
        /// Microsoft's Cowork usage report, restated as a month. Zero, from nobody, when there are none.
        ///
        /// <para>The Cowork estimate's sense check, not one of its inputs. The people not yet running
        /// Cowork are modelled from their own activity (<see cref="CoworkActivities"/>), and this is the
        /// one measured figure that model can be held against. Computed once over every scored seat
        /// holder and shared by both cohorts, so they quote the same comparison.</para>
        /// </summary>
        public static CoworkTaskRate CoworkObservedTaskRate(
            IEnumerable<CoworkReadinessRow> rows,
            int reportPeriodDays,
            CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var perMonth = CoworkTasksPerMonthFactor(reportPeriodDays, o);
            var rate = new CoworkTaskRate();
            if (perMonth <= 0 || rows == null) return rate;

            double tasks = 0;
            foreach (var row in rows)
            {
                var count = row?.CoworkReportTotalTasks.GetValueOrDefault() ?? 0;
                if (count <= 0) continue;
                rate.Users++;
                tasks += count;
            }

            if (rate.Users > 0)
            {
                rate.TasksPerPersonPerMonth = Round(tasks * perMonth / rate.Users, 1);
            }

            return rate;
        }

        /// <summary>
        /// Builds the modelled Cowork estimate for a cohort: the time Cowork could give back on top of what
        /// these people's Copilot licences already save.
        ///
        /// <b>Every output is an assumption applied to observed use.</b> The Cowork tasks already in
        /// Microsoft's report are real, and so is the work everyone else already does by hand - the
        /// meetings they organise and attend, the email they send, their Teams messages, the files they
        /// work on. How much of that work they would hand to Cowork, and how many minutes Cowork would save
        /// on each piece, are a model, and this method returns the assumptions that produced it so no
        /// caller can render a number without them. It deliberately stops at hours: see the note on
        /// <see cref="CoworkValueEstimate"/> for why a monetary figure is not produced.
        /// </summary>
        /// <param name="cohort">
        /// The users the estimate covers: the recommended rollout cohort for
        /// <see cref="CopilotAdoptionSummary.CoworkValueEstimate"/>, or every scored seat holder for
        /// <see cref="CopilotAdoptionSummary.CoworkFullRolloutEstimate"/>.
        /// </param>
        /// <param name="options">Tuning, including every share and minutes-saved assumption.</param>
        /// <param name="coworkReportPeriodDays">
        /// The period of the Cowork usage-report snapshot the rows' task counts came from; zero when there
        /// is none, in which case no Cowork task is treated as observed and everyone is modelled from
        /// their activity instead.
        /// </param>
        /// <param name="observedRate">
        /// The tenant's own Cowork users' average, for the sense check. Computed from
        /// <paramref name="cohort"/> when omitted.
        /// </param>
        public static CoworkValueEstimate EstimateCoworkValue(
            IReadOnlyCollection<CoworkReadinessRow> cohort,
            CopilotAdoptionOptions options = null,
            int coworkReportPeriodDays = 0,
            CoworkTaskRate observedRate = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            if (cohort == null || cohort.Count == 0)
            {
                return new CoworkValueEstimate();
            }

            // Cowork task counts are totals over the report's period, so they are restated onto the same
            // month the rest of the report quotes by the ratio of the two. The usage-report volumes are
            // per-active-day averages (CopilotAdoptionSql.CoworkReadinessSql, unrounded), restated over the same
            // working days a month as the licence estimate's.
            var tasksPerMonth = CoworkTasksPerMonthFactor(coworkReportPeriodDays, o);
            var workingDaysPerMonth = WorkingDaysPerMonth(o);

            double observedTasks = 0;
            var observedTaskUsers = 0;
            var volumes = new double[CoworkActivities.All.Count];
            foreach (var row in cohort)
            {
                var tasks = row.CoworkReportTotalTasks.GetValueOrDefault();
                if (tasksPerMonth > 0 && tasks > 0)
                {
                    // Counted at the tasks they actually run. Their activity is not modelled as well:
                    // that would credit the same person's time twice.
                    observedTaskUsers++;
                    observedTasks += tasks * tasksPerMonth;
                    continue;
                }

                for (var i = 0; i < volumes.Length; i++)
                {
                    volumes[i] += CoworkActivities.All[i].PerActiveDay(row) * workingDaysPerMonth;
                }
            }

            var inputs = new CoworkTaskInputs
            {
                ObservedUsers = observedTaskUsers,
                ObservedTasksPerMonth = observedTasks,
                ObservedRate = observedRate ?? CoworkObservedTaskRate(cohort, coworkReportPeriodDays, o),
            };

            for (var i = 0; i < volumes.Length; i++)
            {
                inputs.ActivityVolumes[CoworkActivities.All[i].Key] = volumes[i];
            }

            return ModelCoworkValue(cohort.Count, o, inputs);
        }

        /// <summary>
        /// Applies the Cowork assumptions to a cohort: the tasks already in Microsoft's Cowork usage report
        /// at the minutes each is assumed to save, plus - for everyone else - each kind of work they
        /// already do by hand, times the share of it they are assumed to hand to Cowork, times the minutes
        /// Cowork is assumed to save on each piece. All of it on top of Copilot.
        ///
        /// <para>Split out of <see cref="EstimateCoworkValue"/> so an estimate can be restated under a
        /// reader's own assumptions without re-running the analysis: the inputs are all it needs, and the
        /// Excel export does exactly that with the figures the reader entered in the portal.</para>
        ///
        /// <para><b>The hours are computed from the ROUNDED volumes it publishes</b>, not the unrounded
        /// sums, and summed in <see cref="CoworkActivities.All"/> order. The portal recomputes the hours in
        /// the browser from the published figures whenever the reader changes an assumption, so the server
        /// has to use the same operands in the same order for an uncustomised page and its Excel report to
        /// agree to the hour.</para>
        ///
        /// <para><b>Why there is no Copilot layer here.</b> These people already hold a Copilot licence,
        /// so the time Copilot saves them is the licence's, not Cowork's: enabling Cowork does not unlock
        /// it, and no decision hangs on it. The minutes are Cowork's increment over Copilot alone. No study
        /// has measured them, alone or for people who already use Copilot, and the assumptions say so. The
        /// Copilot minutes size the licence decision instead - see <see cref="ModelLicenceValue"/>.</para>
        /// </summary>
        /// <param name="inputs">
        /// The cohort's observed Cowork tasks and everyone else's work done by hand a month. Null means
        /// nothing observed and nothing done - a cohort that models to zero hours.
        /// </param>
        public static CoworkValueEstimate ModelCoworkValue(
            int cohortUsers,
            CopilotAdoptionOptions options = null,
            CoworkTaskInputs inputs = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var estimate = new CoworkValueEstimate();

            if (cohortUsers <= 0)
            {
                return estimate;
            }

            var given = inputs ?? new CoworkTaskInputs();
            estimate.CohortUsers = cohortUsers;
            estimate.CoworkTaskUsers = Math.Min(cohortUsers, Math.Max(0, given.ObservedUsers));
            estimate.ObservedCoworkTasks = estimate.CoworkTaskUsers > 0
                ? Round(NonNegative(given.ObservedTasksPerMonth), 0)
                : 0;
            estimate.ProjectedCoworkUsers = cohortUsers - estimate.CoworkTaskUsers;

            var rate = given.ObservedRate ?? new CoworkTaskRate();
            estimate.ObservedTaskRateUsers = Math.Max(0, rate.Users);
            estimate.ObservedTasksPerPersonPerMonth = estimate.ObservedTaskRateUsers > 0
                ? NonNegative(rate.TasksPerPersonPerMonth)
                : 0;

            var taskMinutes = NonNegative(o.CoworkMinutesSavedPerTask);
            var lowerRatio = TimeSavedLowerBoundRatio(o);

            // observed tasks x minutes per task, plus, kind by kind: volume x share x minutes
            var minutes = estimate.ObservedCoworkTasks * taskMinutes;
            double handedOver = 0;
            foreach (var activity in CoworkActivities.All)
            {
                double volume = 0;
                if (estimate.ProjectedCoworkUsers > 0 && given.ActivityVolumes != null)
                {
                    given.ActivityVolumes.TryGetValue(activity.Key, out volume);
                }

                var published = Round(NonNegative(volume), 0);
                estimate.Activities.Add(new CoworkActivityVolume { Activity = activity.Key, VolumePerMonth = published });

                var pieces = published * activity.Share(o);
                handedOver += pieces;
                minutes += pieces * activity.Minutes(o);
            }

            estimate.ProjectedCoworkTasks = Round(handedOver, 0);
            estimate.CoworkTasks = estimate.ObservedCoworkTasks + estimate.ProjectedCoworkTasks;
            estimate.HoursPerMonthHigh = Round(minutes / 60d, 0);
            estimate.HoursPerMonthLow = Round(minutes * lowerRatio / 60d, 0);

            var organise = CoworkActivities.Find(CoworkActivities.OrganiseMeetings);
            var prepare = CoworkActivities.Find(CoworkActivities.PrepareMeetings);
            var email = CoworkActivities.Find(CoworkActivities.SendEmail);
            var teams = CoworkActivities.Find(CoworkActivities.PostInTeams);
            var documents = CoworkActivities.Find(CoworkActivities.CreateDocuments);

            estimate.Assumptions.Add(
                $"Assumes Cowork saves {Num(organise.Minutes(o))} minutes on each meeting it organises, "
                + $"{Num(prepare.Minutes(o))} on each meeting it prepares someone for, {Num(email.Minutes(o))} on "
                + $"each email it sends, {Num(teams.Minutes(o))} on each Teams message it posts, "
                + $"{Num(documents.Minutes(o))} on each document it creates and {Num(taskMinutes)} on each Cowork "
                + "task already in Microsoft's report, on top of what Microsoft 365 Copilot already saves - "
                + "Cowork's increment over Copilot alone. No study has yet measured Cowork's time savings, alone "
                + "or for people who already use Copilot, so these figures are assumptions.");

            estimate.Assumptions.Add(
                $"Assumes people hand Cowork {Percent(organise.Share(o))} of the meetings they organise, "
                + $"{Percent(prepare.Share(o))} of the meetings they attend, {Percent(email.Share(o))} of the "
                + $"emails they send, {Percent(teams.Share(o))} of their Teams messages and "
                + $"{Percent(documents.Share(o))} of the files they work on. No study has measured how much "
                + "work people hand to Cowork either, so these shares are assumptions too.");

            estimate.Assumptions.Add(
                $"Covers {cohortUsers:N0} Copilot seat holder{Plural(cohortUsers)}. The work people already "
                + $"do comes from Microsoft's usage reports, restated over {Num(WorkingDaysPerMonth(o))} working "
                + "days a month; Cowork tasks from Microsoft's Cowork usage report are restated as a "
                + $"{Math.Max(1, o.HabitBucketNormalisationDays)}-day month.");

            if (estimate.CoworkTaskUsers > 0)
            {
                estimate.Assumptions.Add(
                    "Cowork tasks already in Microsoft's Cowork usage report are counted as reported: "
                    + $"{estimate.ObservedCoworkTasks:N0} a month from the {estimate.CoworkTaskUsers:N0} "
                    + (estimate.CoworkTaskUsers == 1 ? "person" : "people")
                    + " running them. Only everyone else's work is modelled, so nobody is counted twice.");
            }
            else
            {
                estimate.Assumptions.Add(
                    "Nobody here has Cowork tasks in Microsoft's Cowork usage report yet, so everyone is "
                    + "modelled from the work they already do.");
            }

            estimate.Assumptions.Add(
                "The minutes are Cowork's increment over Copilot alone. For work Copilot already speeds up - a "
                + "meeting recap, a drafted reply - only the time beyond Copilot's own saving belongs to Cowork.");

            estimate.Assumptions.Add(
                "Only work Microsoft's usage reports count is modelled. Research, searches, replies to "
                + "invitations and scheduled automations are left out, so the estimate understates what Cowork "
                + "does rather than inventing it.");

            estimate.Assumptions.Add(
                $"The lower bound applies {Num(lowerRatio * 100d)}% of the minutes saved; the upper bound applies "
                + "them in full.");

            estimate.Assumptions.Add(
                "This is the potential at full use, not the gain over today: people already using Cowork may be "
                + "realising part of it now.");

            estimate.Assumptions.Add(
                "Time saved is NOT measured by this product and cannot be. These figures are a model for "
                + "sizing a Cowork rollout, not a result.");

            estimate.Assumptions.Add(NoMonetaryValueAssumption);

            return estimate;
        }

        /// <summary>
        /// The Cowork estimate's high-end hours split by where they come from: one part per kind of work in
        /// <see cref="CoworkActivities.All"/> order, then the Cowork tasks already observed.
        ///
        /// <para>Apportioned by largest remainder so the parts add up to exactly
        /// <see cref="CoworkValueEstimate.HoursPerMonthHigh"/>, for the same reason as
        /// <see cref="LicenceHoursByActivity"/>. The portal splits its bar the same way, in the same order.</para>
        /// </summary>
        public static double[] CoworkHoursByActivity(CoworkValueEstimate estimate, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var parts = new double[CoworkActivities.All.Count + 1];
            if (estimate == null || estimate.CohortUsers <= 0) return parts;

            for (var i = 0; i < CoworkActivities.All.Count; i++)
            {
                var activity = CoworkActivities.All[i];
                parts[i] = CoworkVolume(estimate, activity.Key) * activity.Share(o) * activity.Minutes(o) / 60d;
            }

            parts[parts.Length - 1] = estimate.ObservedCoworkTasks * NonNegative(o.CoworkMinutesSavedPerTask) / 60d;
            return Apportion(estimate.HoursPerMonthHigh, parts);
        }

        /// <summary>
        /// The pieces of work a month handed to Cowork, one part per kind of work in
        /// <see cref="CoworkActivities.All"/> order, apportioned so they add up to exactly
        /// <see cref="CoworkValueEstimate.ProjectedCoworkTasks"/>.
        /// </summary>
        public static double[] CoworkTasksByActivity(CoworkValueEstimate estimate, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var parts = new double[CoworkActivities.All.Count];
            if (estimate == null || estimate.CohortUsers <= 0) return parts;

            for (var i = 0; i < CoworkActivities.All.Count; i++)
            {
                parts[i] = CoworkVolume(estimate, CoworkActivities.All[i].Key) * CoworkActivities.All[i].Share(o);
            }

            return Apportion(estimate.ProjectedCoworkTasks, parts);
        }

        /// <summary>The published monthly volume of one kind of work; zero when the estimate does not carry it.</summary>
        public static double CoworkVolume(CoworkValueEstimate estimate, string activity)
        {
            var entry = estimate?.Activities?.FirstOrDefault(a => string.Equals(a?.Activity, activity, StringComparison.Ordinal));
            return entry == null ? 0d : NonNegative(entry.VolumePerMonth);
        }

        /// <summary>A share as a whole-or-decimal percentage, invariant culture: 0.25 -> "25%".</summary>
        private static string Percent(double share)
        {
            return Num(Round(share * 100d, 4)) + "%";
        }

        /// <summary>
        /// The sentence both time-saved estimates end on. One constant, so the two can never disagree about
        /// why neither is priced.
        /// </summary>
        private const string NoMonetaryValueAssumption =
            "No monetary value is shown. Pricing a modelled saving would state a figure this product "
            + "cannot evidence, and it has no defensible fully-loaded hourly rate to price it with. "
            + "This report reports seats, people and hours - never money.";

        /// <summary>A configured or entered figure, with a non-number or a negative read as zero.</summary>
        private static double NonNegative(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : Math.Max(0d, value);
        }

        private static string Plural(long count)
        {
            return count == 1 ? string.Empty : "s";
        }

        #endregion

        #region Licence value estimate (MODELLED - not measured)

        /// <summary>
        /// Builds the modelled licence estimate for a set of licence candidates: the time Microsoft 365
        /// Copilot could give back to them if they were licensed.
        ///
        /// <b>Every output is an assumption applied to observed volume.</b> The volumes are real - they
        /// come from Microsoft's usage reports - but the conversion to time saved is a model, and this
        /// method returns the assumptions that produced it so no caller can render a number without them.
        /// It deliberately stops at hours: see the note on <see cref="CoworkValueEstimate"/> for why a
        /// monetary figure is not produced.
        /// </summary>
        /// <param name="candidates">
        /// The candidates the estimate covers: every recommended candidate for
        /// <see cref="CopilotAdoptionSummary.LicenceOpportunityEstimate"/>, or those already using Copilot
        /// Chat for <see cref="CopilotAdoptionSummary.LicenceChatUsersEstimate"/>.
        /// </param>
        /// <param name="options">Tuning, including the Copilot minutes-saved assumptions.</param>
        /// <param name="candidatesCapped">
        /// True when the candidate query hit <see cref="CopilotAdoptionOptions.MaxOpportunityCandidates"/>,
        /// so the estimate is a floor rather than a total - and says so.
        /// </param>
        public static LicenceValueEstimate EstimateLicenceValue(
            IReadOnlyCollection<LicenceOpportunityRow> candidates,
            CopilotAdoptionOptions options = null,
            bool candidatesCapped = false)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            if (candidates == null || candidates.Count == 0)
            {
                return new LicenceValueEstimate { CandidatesCapped = candidatesCapped };
            }

            // Both the opportunity query and the Cowork readiness query reduce Graph's daily reports to a
            // per-active-day average (CopilotAdoptionSql.PerActiveDay), so the same working-days multiplier
            // restates them as the month every modelled figure in this report is quoted in.
            var workingDaysPerMonth = WorkingDaysPerMonth(o);

            double meetings = 0, mail = 0, documents = 0;
            foreach (var row in candidates)
            {
                meetings += row.TeamsMeetings * workingDaysPerMonth;
                mail += (row.EmailsSent + row.EmailsRead) * workingDaysPerMonth;
                documents += row.FilesViewedOrEdited * workingDaysPerMonth;
            }

            return ModelLicenceValue(candidates.Count, meetings, mail, documents, o, candidatesCapped);
        }

        /// <summary>
        /// Applies the Copilot minutes-saved assumptions to a cohort's observed monthly volumes: meetings,
        /// emails and documents, each times the minutes Microsoft 365 Copilot is assumed to save on it.
        ///
        /// <para>Split out of <see cref="EstimateLicenceValue"/> so an estimate can be restated under a
        /// reader's own assumptions without re-running the analysis: the volumes are all it needs, and the
        /// Excel export does exactly that with the figures the reader entered in the portal.</para>
        ///
        /// <para><b>The hours are computed from the ROUNDED volumes it publishes</b>, not the unrounded
        /// sums. The portal recomputes the hours in the browser from those published volumes whenever the
        /// reader changes an assumption, so the server has to use the same operands for an uncustomised
        /// page and its Excel report to agree to the hour. The difference is at most a fraction of an
        /// hour; the disagreement it prevents is a visible one.</para>
        ///
        /// <para><b>Why the licence decision gets these minutes.</b> Each default is derived from
        /// Microsoft's published Copilot credits and checked against published studies of Microsoft 365
        /// Copilot - the largest of which randomised who received a licence, which is exactly the decision
        /// this figure sizes.</para>
        /// </summary>
        public static LicenceValueEstimate ModelLicenceValue(
            int cohortUsers,
            double meetingsPerMonth,
            double mailPerMonth,
            double documentsPerMonth,
            CopilotAdoptionOptions options = null,
            bool candidatesCapped = false)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var licenceEstimate = new LicenceValueEstimate { CandidatesCapped = candidatesCapped };

            if (cohortUsers <= 0)
            {
                return licenceEstimate;
            }

            licenceEstimate.CohortUsers = cohortUsers;
            licenceEstimate.AddressableMeetings = Round(NonNegative(meetingsPerMonth), 0);
            licenceEstimate.AddressableMailThreads = Round(NonNegative(mailPerMonth), 0);
            licenceEstimate.AddressableDocuments = Round(NonNegative(documentsPerMonth), 0);

            var meetingMinutes = NonNegative(o.CopilotMinutesSavedPerMeeting);
            var emailMinutes = NonNegative(o.CopilotMinutesSavedPerMailThread);
            var documentMinutes = NonNegative(o.CopilotMinutesSavedPerDocument);
            var lowerRatio = TimeSavedLowerBoundRatio(o);

            // Observed items x minutes saved on each.
            var minutes = licenceEstimate.AddressableMeetings * meetingMinutes
                        + licenceEstimate.AddressableMailThreads * emailMinutes
                        + licenceEstimate.AddressableDocuments * documentMinutes;

            licenceEstimate.HoursPerMonthHigh = Round(minutes / 60d, 0);
            licenceEstimate.HoursPerMonthLow = Round(minutes * lowerRatio / 60d, 0);

            licenceEstimate.Assumptions.Add(
                $"Assumes Microsoft 365 Copilot saves {Num(meetingMinutes)} minutes per meeting, "
                + $"{Num(emailMinutes)} per email and {Num(documentMinutes)} per document.");

            licenceEstimate.Assumptions.Add(
                $"Volumes are observed from Microsoft's usage reports for {cohortUsers:N0} recommended licence "
                + $"candidate{Plural(cohortUsers)}, restated over {Num(WorkingDaysPerMonth(o))} working days a "
                + "month.");

            licenceEstimate.Assumptions.Add(
                "Candidates already using Copilot Chat without a licence may be realising part of this "
                + "already, so for them a licence adds less than shown.");

            if (candidatesCapped)
            {
                licenceEstimate.Assumptions.Add(
                    $"The candidate list reached its {o.MaxOpportunityCandidates:N0}-candidate limit, so people "
                    + "beyond it are not counted and the true figure may be higher.");
            }

            licenceEstimate.Assumptions.Add(
                $"The lower bound applies {Num(lowerRatio * 100d)}% of each minutes-saved assumption; the "
                + "upper bound applies them in full.");

            licenceEstimate.Assumptions.Add(
                "This is the potential at full use of a licence, not a forecast: newly licensed people take "
                + "time to build the habit, and not all of them will.");

            licenceEstimate.Assumptions.Add(
                "Time saved is NOT measured by this product and cannot be. These figures are a model for "
                + "sizing a licence purchase, not a result.");

            licenceEstimate.Assumptions.Add(NoMonetaryValueAssumption);

            return licenceEstimate;
        }

        /// <summary>
        /// The licence estimate's high-end hours split into meetings, email and documents, in that order.
        ///
        /// <para>Rounded by largest remainder so the three parts add up to exactly
        /// <see cref="LicenceValueEstimate.HoursPerMonthHigh"/>. Rounding each part on its own lets
        /// "120 + 300 + 181" sit under a total of 600, and a reader checking the sum takes that as an
        /// arithmetic error in the model. The portal apportions the same way.</para>
        /// </summary>
        public static double[] LicenceHoursByActivity(LicenceValueEstimate estimate, CopilotAdoptionOptions options = null)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            if (estimate == null || estimate.CohortUsers <= 0) return new double[3];

            var parts = new[]
            {
                estimate.AddressableMeetings * NonNegative(o.CopilotMinutesSavedPerMeeting) / 60d,
                estimate.AddressableMailThreads * NonNegative(o.CopilotMinutesSavedPerMailThread) / 60d,
                estimate.AddressableDocuments * NonNegative(o.CopilotMinutesSavedPerDocument) / 60d,
            };

            return Apportion(estimate.HoursPerMonthHigh, parts);
        }

        /// <summary>
        /// Splits a rounded total across unrounded parts so the rounded parts add up to it: floor every
        /// part, then give the leftover units to the parts with the largest remainders. Ties go to the
        /// earlier part, so the split is deterministic. The portal's <c>apportion</c> is its twin.
        /// </summary>
        private static double[] Apportion(double total, double[] parts)
        {
            var result = parts.Select(p => Math.Floor(NonNegative(p))).ToArray();
            var leftover = (int)Math.Max(0d, total - result.Sum());

            // Largest remainder first; ties go to the earlier part so the split is deterministic.
            foreach (var index in Enumerable.Range(0, parts.Length)
                         .OrderByDescending(i => NonNegative(parts[i]) - Math.Floor(NonNegative(parts[i])))
                         .ThenBy(i => i))
            {
                if (leftover <= 0) break;
                result[index] += 1;
                leftover--;
            }

            return result;
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

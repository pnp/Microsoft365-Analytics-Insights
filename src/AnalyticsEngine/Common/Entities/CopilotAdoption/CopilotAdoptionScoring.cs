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

        /// <summary>
        /// The single next step for this user, in plain English. Exported in the CSV so the list can be
        /// handed to a department lead and acted on without further interpretation.
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
                        ? $"Sustain and broaden - solid regular use in {AppsPhrase(row.AppsUsed)}; "
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

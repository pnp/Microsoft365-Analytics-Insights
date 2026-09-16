using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// How the licensed-user list is filtered and ordered. A plain object rather than a set of method
    /// arguments so the same shape can come from a query string today and from a scheduled report's
    /// configuration later without changing any of the logic below.
    /// </summary>
    public class LicensedUserQuery
    {
        /// <summary>Free-text match against UPN, mail, department, job title or manager.</summary>
        public string Search { get; set; }

        /// <summary>Restrict to these engagement bands; empty means all.</summary>
        public List<AdoptionBand> Bands { get; set; } = new List<AdoptionBand>();

        /// <summary>
        /// Restrict to these recommended-action codes; empty means all. Separate from
        /// <see cref="Bands"/> because an action is not a band: "Add a second app" spans two bands,
        /// and it is the action that an enablement programme is actually organised around, so the
        /// aggregate plan on the overview has to be able to drill through to exactly the people it
        /// counted rather than to an approximation of them.
        /// </summary>
        public List<string> Actions { get; set; } = new List<string>();

        public string Department { get; set; }

        public string Country { get; set; }

        /// <summary>
        /// Restrict to one reclaim confidence tier. This is the drill-through key for the headline
        /// reclaim aggregates, so the list population is exactly the one the KPI counted.
        /// </summary>
        public string ReclaimEligibility { get; set; }

        /// <summary>Only users who have used Microsoft 365 Copilot Cowork.</summary>
        public bool CoworkOnly { get; set; }

        /// <summary>Only users whose account is disabled - the clearest reclaim candidates of all.</summary>
        public bool DisabledAccountsOnly { get; set; }

        public double? MinScore { get; set; }

        public double? MaxScore { get; set; }

        /// <summary>One of <see cref="LicensedUserSortFields"/>.</summary>
        public string SortBy { get; set; } = LicensedUserSortFields.Score;

        /// <summary>
        /// Ascending by default for the score, because the whole point of the list is finding the
        /// people who are <i>not</i> using their licence.
        /// </summary>
        public bool SortDescending { get; set; }
    }

    /// <summary>Sortable columns of the licensed-user list. An allow-list: nothing here reaches SQL.</summary>
    public static class LicensedUserSortFields
    {
        public const string Score = "score";
        public const string UserPrincipalName = "upn";
        public const string Interactions = "interactions";
        public const string ActiveDays = "activeDays";
        public const string LastUse = "lastUse";
        public const string Department = "department";
        public const string Cowork = "cowork";

        // Added so every column in the on-screen table can be sorted by clicking its header. A column
        // the user can see but not sort reads as a bug, and the list is long enough that "find the
        // people in one band" or "group the report-sourced rows together" are real questions.
        public const string Band = "band";
        public const string Apps = "apps";
        public const string SignalSource = "signalSource";
        public const string ReclaimEligibility = "reclaimEligibility";
        public const string Action = "action";
    }

    /// <summary>How the licence-opportunity list is filtered and ordered.</summary>
    public class LicenceOpportunityQuery
    {
        public string Search { get; set; }

        public string Department { get; set; }

        public string Country { get; set; }

        /// <summary>Only candidates that clear the recommendation threshold.</summary>
        public bool RecommendedOnly { get; set; }

        /// <summary>Only candidates who are already using Copilot Chat without a licence.</summary>
        public bool ExistingCopilotUsersOnly { get; set; }

        public double? MinScore { get; set; }

        /// <summary>One of <see cref="LicenceOpportunitySortFields"/>.</summary>
        public string SortBy { get; set; } = LicenceOpportunitySortFields.Score;

        /// <summary>Descending by default: the strongest business cases first.</summary>
        public bool SortDescending { get; set; } = true;
    }

    /// <summary>Sortable columns of the licence-opportunity list.</summary>
    public static class LicenceOpportunitySortFields
    {
        public const string Score = "score";
        public const string UserPrincipalName = "upn";
        public const string CopilotUse = "copilot";
        public const string Collaboration = "collaboration";
        public const string Email = "email";
        public const string Documents = "documents";
        public const string Department = "department";

        /// <summary>Last recorded Microsoft 365 activity. Added so the column can be sorted from its header.</summary>
        public const string LastM365Activity = "lastM365";
    }

    /// <summary>
    /// How the Cowork readiness list is filtered and ordered.
    ///
    /// Shaped around the action the tab exists to produce: deciding who goes into the Cowork spending
    /// policy. Hence <see cref="RecommendedOnly"/> rather than a score threshold as the primary filter -
    /// the reader is picking people, not tuning a model.
    /// </summary>
    public class CoworkReadinessQuery
    {
        public string Search { get; set; }

        /// <summary>Restrict to these Cowork tiers; empty means all.</summary>
        public List<string> Tiers { get; set; } = new List<string>();

        public string Department { get; set; }

        public string Country { get; set; }

        /// <summary>Only the people who should be in the Cowork spending policy.</summary>
        public bool RecommendedOnly { get; set; }

        /// <summary>Only people who have actually used Cowork - the evidence-backed rows.</summary>
        public bool CoworkUsersOnly { get; set; }

        public double? MinCoordinationLoad { get; set; }

        public double? MinFluency { get; set; }

        /// <summary>One of <see cref="CoworkSortFields"/>.</summary>
        public string SortBy { get; set; } = CoworkSortFields.CoordinationLoad;

        /// <summary>Descending by default: the strongest cases first.</summary>
        public bool SortDescending { get; set; } = true;
    }

    /// <summary>Sortable columns of the Cowork readiness list. An allow-list: nothing here reaches SQL.</summary>
    public static class CoworkSortFields
    {
        public const string CoordinationLoad = "load";
        public const string Fluency = "fluency";
        public const string UserPrincipalName = "upn";
        public const string CoworkInteractions = "coworkInteractions";
        public const string CoworkActiveDays = "coworkActiveDays";
        public const string Meetings = "meetings";
        public const string Department = "department";
        public const string Tier = "tier";
    }

    /// <summary>
    /// Filtering, sorting and paging over an analysis result, plus the CSV export schemas.
    ///
    /// All of it operates on the already-scored, already-materialised lists rather than going back to
    /// the database, which is what guarantees the exported CSV is exactly the data behind the summary
    /// the user is looking at. Nothing is more damaging to a report used in a licence negotiation than
    /// a spreadsheet that disagrees with the chart it was exported from.
    /// </summary>
    public static class CopilotAdoptionExports
    {
        #region Licensed users

        /// <summary>Applies the filter, then the sort. Returns a new list; the source is not mutated.</summary>
        public static List<LicensedUserAdoptionRow> Apply(
            IEnumerable<LicensedUserAdoptionRow> rows,
            LicensedUserQuery query)
        {
            var source = rows ?? Enumerable.Empty<LicensedUserAdoptionRow>();
            var q = query ?? new LicensedUserQuery();

            var filtered = source.Where(row => MatchesLicensedUser(row, q));
            return SortLicensedUsers(filtered, q).ToList();
        }

        private static bool MatchesLicensedUser(LicensedUserAdoptionRow row, LicensedUserQuery q)
        {
            if (q.Bands != null && q.Bands.Count > 0 && !q.Bands.Contains(row.Band))
            {
                return false;
            }

            if (q.Actions != null && q.Actions.Count > 0 &&
                !q.Actions.Contains(row.RecommendedActionCode ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (q.CoworkOnly && !row.UsedCowork) return false;

            if (!string.IsNullOrWhiteSpace(q.ReclaimEligibility) &&
                !string.Equals(row.ReclaimEligibility ?? string.Empty, q.ReclaimEligibility.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // AccountEnabled is nullable: null means "we have not imported that flag", which is not the
            // same as "disabled", so it must not be swept into a reclaim list.
            if (q.DisabledAccountsOnly && row.AccountEnabled != false) return false;

            if (q.MinScore.HasValue && row.AdoptionScore < q.MinScore.Value) return false;
            if (q.MaxScore.HasValue && row.AdoptionScore > q.MaxScore.Value) return false;

            if (!EqualsOrEmpty(q.Department, row.Department)) return false;
            if (!EqualsOrEmpty(q.Country, row.Country)) return false;

            return MatchesSearch(q.Search,
                row.UserPrincipalName, row.Mail, row.Department, row.JobTitle,
                row.ManagerUserPrincipalName, row.OfficeLocation, row.CompanyName);
        }

        private static IEnumerable<LicensedUserAdoptionRow> SortLicensedUsers(
            IEnumerable<LicensedUserAdoptionRow> rows,
            LicensedUserQuery q)
        {
            // The tie-break is always UPN so paging is stable: without it, two users on the same score
            // can swap places between page 1 and page 2 and appear twice (or not at all) in an export.
            switch (q.SortBy)
            {
                case LicensedUserSortFields.UserPrincipalName:
                    return Order(rows, r => r.UserPrincipalName ?? string.Empty, q.SortDescending);
                case LicensedUserSortFields.Interactions:
                    return OrderThenUpn(rows, r => (double)r.Interactions, q.SortDescending);
                case LicensedUserSortFields.ActiveDays:
                    return OrderThenUpn(rows, r => (double)r.ActiveDays, q.SortDescending);
                case LicensedUserSortFields.LastUse:
                    // Never-used sorts as the beginning of time so it lands at the "needs attention"
                    // end of an ascending sort rather than being scattered by a null.
                    return OrderThenUpn(rows, r => (r.LastInteractionUtc ?? DateTime.MinValue).Ticks, q.SortDescending);
                case LicensedUserSortFields.Department:
                    return Order(rows, r => r.Department ?? string.Empty, q.SortDescending);
                case LicensedUserSortFields.Cowork:
                    return OrderThenUpn(rows, r => (double)r.CoworkInteractions, q.SortDescending);
                case LicensedUserSortFields.Band:
                    // By the underlying band value, not its display name, so the order is the adoption
                    // ladder (Never used -> Champion) rather than the alphabet.
                    return OrderThenUpn(rows, r => (int)r.Band, q.SortDescending);
                case LicensedUserSortFields.Apps:
                    return OrderThenUpn(rows, r => (double)r.AppsUsed, q.SortDescending);
                case LicensedUserSortFields.SignalSource:
                    return Order(rows, r => r.SignalSource ?? string.Empty, q.SortDescending);
                case LicensedUserSortFields.ReclaimEligibility:
                    // Grouped by how confident the reclaim is, most actionable first ascending, rather
                    // than alphabetically - "certain" before "probable" before "review" is the order an
                    // admin works through, and it is not the order the words happen to sort in.
                    return OrderThenUpn(rows, r => ReclaimTierRank(r.ReclaimEligibility), q.SortDescending);
                case LicensedUserSortFields.Action:
                    return Order(rows, r => r.RecommendedActionLabel ?? string.Empty, q.SortDescending);
                default:
                    return OrderThenUpn(rows, r => r.AdoptionScore, q.SortDescending);
            }
        }

        /// <summary>
        /// Orders the reclaim tiers by how safe they are to act on rather than alphabetically. Rows with
        /// no tier (the actively engaged) sort last, because they are not a reclaim decision at all.
        /// </summary>
        private static int ReclaimTierRank(string tier)
        {
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Review, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Excluded, StringComparison.OrdinalIgnoreCase)) return 3;
            return 4;
        }

        /// <summary>
        /// The licensed-user CSV. Column order tells the story the export exists to tell: who they are,
        /// how much they are using Copilot, how that scores, and what to do about it.
        /// </summary>
        public static IReadOnlyList<CsvColumn<LicensedUserAdoptionRow>> LicensedUserColumns(
            bool figuresIncomplete = false,
            string warningSummary = null)
        {
            return new List<CsvColumn<LicensedUserAdoptionRow>>
            {
                new CsvColumn<LicensedUserAdoptionRow>("User principal name", r => r.UserPrincipalName),
                new CsvColumn<LicensedUserAdoptionRow>("Email", r => r.Mail),
                new CsvColumn<LicensedUserAdoptionRow>("Department", r => r.Department),
                new CsvColumn<LicensedUserAdoptionRow>("Job title", r => r.JobTitle),
                new CsvColumn<LicensedUserAdoptionRow>("Manager", r => r.ManagerUserPrincipalName),
                new CsvColumn<LicensedUserAdoptionRow>("Office", r => r.OfficeLocation),
                new CsvColumn<LicensedUserAdoptionRow>("Country", r => r.Country),
                new CsvColumn<LicensedUserAdoptionRow>("Company", r => r.CompanyName),
                new CsvColumn<LicensedUserAdoptionRow>("Account enabled", r => r.AccountEnabled),
                new CsvColumn<LicensedUserAdoptionRow>("Account created (UTC)", r => r.AccountCreatedUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Tenure basis", r => r.TenureBasis),
                new CsvColumn<LicensedUserAdoptionRow>("Days since tenure start", r => r.DaysSinceTenureStart),
                new CsvColumn<LicensedUserAdoptionRow>("Too new to judge", r => r.TooNewToJudge),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim eligibility", r => r.ReclaimEligibility),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim eligibility reason", r => r.ReclaimEligibilityReason),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim exclusion reason", r => r.ReclaimExclusionReason),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim exclusion note", r => r.ReclaimExclusionNote),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim excluded by", r => r.ReclaimExcludedBy),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim excluded (UTC)", r => r.ReclaimExcludedUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Reclaim exclusion review after (UTC)", r => r.ReclaimExclusionReviewAfterUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Expired reclaim exclusion", r => r.ReclaimExclusionExpired),
                new CsvColumn<LicensedUserAdoptionRow>("Copilot licences", r => r.SeatLicences),

                new CsvColumn<LicensedUserAdoptionRow>("Adoption score (0-100)", r => r.AdoptionScore),
                new CsvColumn<LicensedUserAdoptionRow>("Engagement band", r => r.BandName),
                new CsvColumn<LicensedUserAdoptionRow>("Signal source", r => r.SignalSource),
                new CsvColumn<LicensedUserAdoptionRow>("Figures incomplete", r => figuresIncomplete),
                new CsvColumn<LicensedUserAdoptionRow>("Figure warnings", r => warningSummary),
                new CsvColumn<LicensedUserAdoptionRow>("Frequency score", r => r.FrequencyScore),
                new CsvColumn<LicensedUserAdoptionRow>("Depth score", r => r.DepthScore),
                new CsvColumn<LicensedUserAdoptionRow>("Breadth score", r => r.BreadthScore),

                new CsvColumn<LicensedUserAdoptionRow>("Interactions in period", r => r.Interactions),
                new CsvColumn<LicensedUserAdoptionRow>("Active days in period", r => r.ActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Active days for full marks", r => r.ExpectedActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Copilot apps used", r => r.AppsUsed),
                new CsvColumn<LicensedUserAdoptionRow>("Source comparison available", r => r.SourceComparisonAvailable),
                new CsvColumn<LicensedUserAdoptionRow>("Audit interactions in selected period", r => r.AuditInteractions),
                new CsvColumn<LicensedUserAdoptionRow>("Audit active days in selected period", r => r.AuditActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Audit apps used in selected period", r => r.AuditAppsUsed),
                new CsvColumn<LicensedUserAdoptionRow>("Copilot agents used", r => r.AgentsUsed),
                new CsvColumn<LicensedUserAdoptionRow>("Used Cowork", r => r.UsedCowork),
                new CsvColumn<LicensedUserAdoptionRow>("Cowork interactions", r => r.CoworkInteractions),

                new CsvColumn<LicensedUserAdoptionRow>("First use (UTC)", r => r.FirstInteractionUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Last use (UTC)", r => r.LastInteractionUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Days since last use", r => r.DaysSinceLastUse),

                new CsvColumn<LicensedUserAdoptionRow>("Microsoft report prompts", r => r.ReportPrompts),
                new CsvColumn<LicensedUserAdoptionRow>("Microsoft report active days", r => r.ReportActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Microsoft report last activity", r => r.ReportLastActivityUtc),

                new CsvColumn<LicensedUserAdoptionRow>("Recommended action", r => r.RecommendedActionLabel),
                new CsvColumn<LicensedUserAdoptionRow>("Recommended action detail", r => r.RecommendedAction),
            };
        }

        #endregion

        #region Licence opportunities

        public static List<LicenceOpportunityRow> Apply(
            IEnumerable<LicenceOpportunityRow> rows,
            LicenceOpportunityQuery query)
        {
            var source = rows ?? Enumerable.Empty<LicenceOpportunityRow>();
            var q = query ?? new LicenceOpportunityQuery();

            var filtered = source.Where(row => MatchesOpportunity(row, q));
            return SortOpportunities(filtered, q).ToList();
        }

        private static bool MatchesOpportunity(LicenceOpportunityRow row, LicenceOpportunityQuery q)
        {
            if (q.RecommendedOnly && !row.Recommended) return false;
            if (q.ExistingCopilotUsersOnly && row.UnlicensedCopilotInteractions <= 0) return false;
            if (q.MinScore.HasValue && row.OpportunityScore < q.MinScore.Value) return false;

            if (!EqualsOrEmpty(q.Department, row.Department)) return false;
            if (!EqualsOrEmpty(q.Country, row.Country)) return false;

            return MatchesSearch(q.Search,
                row.UserPrincipalName, row.Mail, row.Department, row.JobTitle,
                row.ManagerUserPrincipalName, row.OfficeLocation, row.CompanyName);
        }

        private static IEnumerable<LicenceOpportunityRow> SortOpportunities(
            IEnumerable<LicenceOpportunityRow> rows,
            LicenceOpportunityQuery q)
        {
            switch (q.SortBy)
            {
                case LicenceOpportunitySortFields.UserPrincipalName:
                    return Order(rows, r => r.UserPrincipalName ?? string.Empty, q.SortDescending);
                case LicenceOpportunitySortFields.CopilotUse:
                    return OrderThenUpn(rows, r => (double)r.UnlicensedCopilotInteractions, q.SortDescending);
                case LicenceOpportunitySortFields.Collaboration:
                    return OrderThenUpn(rows, r => (double)(r.TeamsMessages + r.TeamsMeetings), q.SortDescending);
                case LicenceOpportunitySortFields.Email:
                    return OrderThenUpn(rows, r => (double)(r.EmailsSent + r.EmailsRead), q.SortDescending);
                case LicenceOpportunitySortFields.Documents:
                    return OrderThenUpn(rows, r => (double)r.FilesViewedOrEdited, q.SortDescending);
                case LicenceOpportunitySortFields.Department:
                    return Order(rows, r => r.Department ?? string.Empty, q.SortDescending);
                case LicenceOpportunitySortFields.LastM365Activity:
                    // Never-seen sorts as the beginning of time rather than being scattered by a null.
                    return OrderThenUpn(rows, r => (r.LastM365ActivityUtc ?? DateTime.MinValue).Ticks, q.SortDescending);
                default:
                    // Proven demand first, then the composite score. The database already sorts
                    // proven-demand candidates into the TOP (@maxRows) window ahead of merely busy
                    // users; re-sorting on score alone in memory would undo that and put a person who
                    // demonstrably uses Copilot below one who never has. The Copilot component (35) is
                    // worth less than the recommendation bar (50), so that inversion is the normal
                    // case, not an edge case.
                    //
                    // Applied as the primary key of one composite sort, not as a separate pass: a
                    // later OrderBy would discard it except within exact score ties.
                    return ProvenDemandFirst(rows)
                        .ThenBy(r => r.OpportunityScore, Direction(q.SortDescending))
                        .ThenBy(r => r.UserPrincipalName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Orders proven-demand candidates ahead of workload-inferred ones, whichever direction the
        /// score is then sorted in. Evidence outranks inference in this list at all times - that is the
        /// whole reason the tier exists.
        /// </summary>
        private static IOrderedEnumerable<LicenceOpportunityRow> ProvenDemandFirst(
            IEnumerable<LicenceOpportunityRow> rows)
        {
            return rows.OrderBy(r =>
                r.QualificationTier == CopilotAdoptionScoring.OpportunityTiers.ProvenDemand ? 0 : 1);
        }

        /// <summary>Ascending or descending as a comparer, so it can be used inside a ThenBy.</summary>
        private static IComparer<double> Direction(bool descending)
        {
            return descending ? DescendingDouble.Instance : (IComparer<double>)Comparer<double>.Default;
        }

        private sealed class DescendingDouble : IComparer<double>
        {
            public static readonly DescendingDouble Instance = new DescendingDouble();

            public int Compare(double x, double y)
            {
                return y.CompareTo(x);
            }
        }

        /// <summary>
        /// The licence-opportunity CSV. Leads with the evidence (existing unlicensed Copilot use) and
        /// ends with a ready-written justification, so the file can go straight to whoever signs off
        /// the spend.
        /// </summary>
        public static IReadOnlyList<CsvColumn<LicenceOpportunityRow>> LicenceOpportunityColumns(
            bool figuresIncomplete = false,
            string warningSummary = null)
        {
            return new List<CsvColumn<LicenceOpportunityRow>>
            {
                new CsvColumn<LicenceOpportunityRow>("User principal name", r => r.UserPrincipalName),
                new CsvColumn<LicenceOpportunityRow>("Email", r => r.Mail),
                new CsvColumn<LicenceOpportunityRow>("Department", r => r.Department),
                new CsvColumn<LicenceOpportunityRow>("Job title", r => r.JobTitle),
                new CsvColumn<LicenceOpportunityRow>("Manager", r => r.ManagerUserPrincipalName),
                new CsvColumn<LicenceOpportunityRow>("Office", r => r.OfficeLocation),
                new CsvColumn<LicenceOpportunityRow>("Country", r => r.Country),
                new CsvColumn<LicenceOpportunityRow>("Company", r => r.CompanyName),

                new CsvColumn<LicenceOpportunityRow>("Opportunity score (0-100)", r => r.OpportunityScore),
                new CsvColumn<LicenceOpportunityRow>("Recommended for a licence", r => r.Recommended),
                // Whether the recommendation rests on evidence (the person already uses Copilot) or on
                // inference (they look like someone who would). Those justify a purchase very
                // differently, and "recommended" alone does not say which.
                new CsvColumn<LicenceOpportunityRow>("Qualified by", r => r.QualificationTierLabel),
                new CsvColumn<LicenceOpportunityRow>("Figures incomplete", r => figuresIncomplete),
                new CsvColumn<LicenceOpportunityRow>("Figure warnings", r => warningSummary),

                new CsvColumn<LicenceOpportunityRow>("Unlicensed Copilot interactions", r => r.UnlicensedCopilotInteractions),
                new CsvColumn<LicenceOpportunityRow>("Unlicensed Copilot active days", r => r.UnlicensedCopilotActiveDays),
                new CsvColumn<LicenceOpportunityRow>("Last Copilot use (UTC)", r => r.LastCopilotInteractionUtc),

                new CsvColumn<LicenceOpportunityRow>("Teams messages per active day", r => r.TeamsMessages),
                new CsvColumn<LicenceOpportunityRow>("Teams meetings per active day", r => r.TeamsMeetings),
                new CsvColumn<LicenceOpportunityRow>("Emails sent per active day", r => r.EmailsSent),
                new CsvColumn<LicenceOpportunityRow>("Emails read per active day", r => r.EmailsRead),
                new CsvColumn<LicenceOpportunityRow>("Files viewed or edited per active day", r => r.FilesViewedOrEdited),
                new CsvColumn<LicenceOpportunityRow>("Last Microsoft 365 activity", r => r.LastM365ActivityUtc),

                new CsvColumn<LicenceOpportunityRow>("Copilot demand score", r => r.CopilotDemandScore),
                new CsvColumn<LicenceOpportunityRow>("Collaboration score", r => r.CollaborationScore),
                new CsvColumn<LicenceOpportunityRow>("Email score", r => r.EmailScore),
                new CsvColumn<LicenceOpportunityRow>("Document score", r => r.DocumentScore),

                new CsvColumn<LicenceOpportunityRow>("Justification", r => r.Rationale),
            };
        }

        #endregion

        #region Cowork readiness

        /// <summary>Applies the filter, then the sort. Returns a new list; the source is not mutated.</summary>
        public static List<CoworkReadinessRow> Apply(
            IEnumerable<CoworkReadinessRow> rows,
            CoworkReadinessQuery query)
        {
            var source = rows ?? Enumerable.Empty<CoworkReadinessRow>();
            var q = query ?? new CoworkReadinessQuery();

            var filtered = source.Where(row => MatchesCowork(row, q));
            return SortCowork(filtered, q).ToList();
        }

        private static bool MatchesCowork(CoworkReadinessRow row, CoworkReadinessQuery q)
        {
            if (q.RecommendedOnly && !row.RecommendForPolicy) return false;
            if (q.CoworkUsersOnly && !row.UsedCowork) return false;
            if (q.MinCoordinationLoad.HasValue && row.CoordinationLoadScore < q.MinCoordinationLoad.Value) return false;
            if (q.MinFluency.HasValue && row.FluencyScore < q.MinFluency.Value) return false;

            if (q.Tiers != null && q.Tiers.Count > 0
                && !q.Tiers.Contains(row.Tier, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!EqualsOrEmpty(q.Department, row.Department)) return false;
            if (!EqualsOrEmpty(q.Country, row.Country)) return false;

            return MatchesSearch(q.Search,
                row.UserPrincipalName, row.Mail, row.Department, row.JobTitle,
                row.ManagerUserPrincipalName, row.OfficeLocation, row.CompanyName);
        }

        private static IEnumerable<CoworkReadinessRow> SortCowork(
            IEnumerable<CoworkReadinessRow> rows,
            CoworkReadinessQuery q)
        {
            switch (q.SortBy)
            {
                case CoworkSortFields.UserPrincipalName:
                    return Order(rows, r => r.UserPrincipalName ?? string.Empty, q.SortDescending);
                case CoworkSortFields.Fluency:
                    return OrderThenUpn(rows, r => r.FluencyScore, q.SortDescending);
                case CoworkSortFields.CoworkInteractions:
                    return OrderThenUpn(rows, r => (double)r.CoworkInteractions, q.SortDescending);
                case CoworkSortFields.CoworkActiveDays:
                    return OrderThenUpn(rows, r => (double)r.CoworkActiveDays, q.SortDescending);
                case CoworkSortFields.Meetings:
                    return OrderThenUpn(rows, r => (double)r.TeamsMeetings, q.SortDescending);
                case CoworkSortFields.Department:
                    return Order(rows, r => r.Department ?? string.Empty, q.SortDescending);
                case CoworkSortFields.Tier:
                    // Ordered by the tier's position in the report order, not alphabetically: "Established"
                    // before "Trialling" before "Prime candidate" is a meaningful progression, whereas an
                    // alphabetical sort would interleave the evidence-backed tiers with the inferred ones.
                    return OrderThenUpn(rows, r => (double)TierRank(r.Tier), q.SortDescending);
                default:
                    return OrderThenUpn(rows, r => r.CoordinationLoadScore, q.SortDescending);
            }
        }

        private static int TierRank(string tier)
        {
            var index = CopilotAdoptionScoring.AllCoworkTiers
                .ToList()
                .FindIndex(t => string.Equals(t, tier, StringComparison.OrdinalIgnoreCase));

            // An unknown tier sorts last rather than first, so a future addition cannot silently take over
            // the top of a list an admin acts on.
            return index < 0 ? int.MaxValue : index;
        }

        /// <summary>
        /// The Cowork CSV, written to be a <b>spending-policy scoping list</b> rather than a data dump.
        ///
        /// <para>UPN first, because that is the column an admin pastes into the policy. Then the verdict
        /// and - immediately next to it - whether that verdict is evidence or a prediction, so the two can
        /// never be read apart. The measured signals follow, and the ready-written justification is last
        /// so a row can be quoted straight into a change request.</para>
        ///
        /// <para>The modelled hours estimate is deliberately <b>absent</b> from this file. It is a
        /// cohort-level figure built from an assumption, and a per-user column of modelled minutes would
        /// be read as a measurement of that individual - which it is not, and which this product cannot
        /// measure. It lives on its own clearly-labelled workbook sheet instead.</para>
        /// </summary>
        public static IReadOnlyList<CsvColumn<CoworkReadinessRow>> CoworkReadinessColumns()
        {
            return new List<CsvColumn<CoworkReadinessRow>>
            {
                new CsvColumn<CoworkReadinessRow>("User principal name", r => r.UserPrincipalName),
                new CsvColumn<CoworkReadinessRow>("Email", r => r.Mail),
                new CsvColumn<CoworkReadinessRow>("Department", r => r.Department),
                new CsvColumn<CoworkReadinessRow>("Job title", r => r.JobTitle),
                new CsvColumn<CoworkReadinessRow>("Manager", r => r.ManagerUserPrincipalName),
                new CsvColumn<CoworkReadinessRow>("Office", r => r.OfficeLocation),
                new CsvColumn<CoworkReadinessRow>("Country", r => r.Country),
                new CsvColumn<CoworkReadinessRow>("Company", r => r.CompanyName),
                new CsvColumn<CoworkReadinessRow>("Account enabled", r => r.AccountEnabled),

                new CsvColumn<CoworkReadinessRow>("Add to Cowork policy", r => r.RecommendForPolicy),
                new CsvColumn<CoworkReadinessRow>("Cowork tier", r => r.TierLabel),
                // Evidence or inference. Sits next to the tier on purpose: "Prime candidate" is a
                // prediction and must never be read as an observation of what the person already does.
                new CsvColumn<CoworkReadinessRow>("Verdict based on", r => r.Basis),

                new CsvColumn<CoworkReadinessRow>("Coordination load (0-100)", r => r.CoordinationLoadScore),
                new CsvColumn<CoworkReadinessRow>("Copilot fluency (0-100)", r => r.FluencyScore),
                new CsvColumn<CoworkReadinessRow>("Copilot adoption score (0-100)", r => r.AdoptionScore),
                new CsvColumn<CoworkReadinessRow>("Copilot agents used", r => r.AgentsUsed),

                new CsvColumn<CoworkReadinessRow>("Cowork interactions", r => r.CoworkInteractions),
                new CsvColumn<CoworkReadinessRow>("Cowork active days", r => r.CoworkActiveDays),
                new CsvColumn<CoworkReadinessRow>("Regular Cowork user", r => r.RegularCoworkUser),
                new CsvColumn<CoworkReadinessRow>("Last Cowork use (UTC)", r => r.LastCoworkInteractionUtc),

                new CsvColumn<CoworkReadinessRow>("Teams meetings per active day", r => r.TeamsMeetings),
                new CsvColumn<CoworkReadinessRow>("Teams messages per active day", r => r.TeamsMessages),
                new CsvColumn<CoworkReadinessRow>("Emails sent per active day", r => r.EmailsSent),
                new CsvColumn<CoworkReadinessRow>("Emails read per active day", r => r.EmailsRead),
                new CsvColumn<CoworkReadinessRow>("Files viewed or edited per active day", r => r.FilesViewedOrEdited),
                new CsvColumn<CoworkReadinessRow>("Last Microsoft 365 activity", r => r.LastM365ActivityUtc),

                new CsvColumn<CoworkReadinessRow>("Meeting score", r => r.MeetingScore),
                new CsvColumn<CoworkReadinessRow>("Collaboration score", r => r.CollaborationScore),
                new CsvColumn<CoworkReadinessRow>("Email score", r => r.EmailScore),
                new CsvColumn<CoworkReadinessRow>("Document score", r => r.DocumentScore),

                // Named "all Copilot Credits", never "Cowork credits". Microsoft meters Cowork against the
                // shared Copilot Credits pool with no per-row workload discriminator, so a Cowork-only
                // figure does not exist. Empty means not attributable - NOT zero.
                new CsvColumn<CoworkReadinessRow>(
                    "All Copilot Credits in period (not Cowork-only; blank = not attributable)",
                    r => r.TotalCopilotCredits),

                new CsvColumn<CoworkReadinessRow>("Justification", r => r.Rationale),
            };
        }

        #endregion

        #region Shared helpers

        /// <summary>
        /// Case-insensitive "contains" across several fields. Ordinal-ignore-case rather than a culture
        /// comparison so a search behaves identically on a Turkish-locale server as on an English one.
        /// </summary>
        private static bool MatchesSearch(string search, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }

            var term = search.Trim();
            return fields.Any(field =>
                !string.IsNullOrEmpty(field)
                && field.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>An unset filter matches everything; a set one is an exact, case-insensitive match.</summary>
        private static bool EqualsOrEmpty(string filter, string value)
        {
            return string.IsNullOrWhiteSpace(filter)
                || string.Equals(filter.Trim(), (value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static IOrderedEnumerable<T> Order<T, TKey>(
            IEnumerable<T> rows, Func<T, TKey> key, bool descending)
        {
            return descending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        }

        private static IOrderedEnumerable<LicensedUserAdoptionRow> OrderThenUpn<TKey>(
            IEnumerable<LicensedUserAdoptionRow> rows, Func<LicensedUserAdoptionRow, TKey> key, bool descending)
        {
            return Order(rows, key, descending)
                .ThenBy(r => r.UserPrincipalName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        private static IOrderedEnumerable<LicenceOpportunityRow> OrderThenUpn<TKey>(
            IEnumerable<LicenceOpportunityRow> rows, Func<LicenceOpportunityRow, TKey> key, bool descending)
        {
            return Order(rows, key, descending)
                .ThenBy(r => r.UserPrincipalName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        private static IOrderedEnumerable<CoworkReadinessRow> OrderThenUpn<TKey>(
            IEnumerable<CoworkReadinessRow> rows, Func<CoworkReadinessRow, TKey> key, bool descending)
        {
            return Order(rows, key, descending)
                .ThenBy(r => r.UserPrincipalName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One page of a filtered list. <paramref name="take"/> is clamped so a stray query string
        /// cannot ask the server to serialise a whole tenant into a single JSON response.
        /// </summary>
        public static List<T> Page<T>(IReadOnlyList<T> rows, int skip, int take, int maxTake = 500)
        {
            if (rows == null || rows.Count == 0) return new List<T>();

            var safeSkip = Math.Max(0, skip);
            var safeTake = Math.Min(Math.Max(1, take), maxTake);

            return rows.Skip(safeSkip).Take(safeTake).ToList();
        }

        #endregion
    }
}

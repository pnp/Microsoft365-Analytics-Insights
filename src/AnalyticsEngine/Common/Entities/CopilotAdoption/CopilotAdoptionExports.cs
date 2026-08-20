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

        public string Department { get; set; }

        public string Country { get; set; }

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

            if (q.CoworkOnly && !row.UsedCowork) return false;

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
                default:
                    return OrderThenUpn(rows, r => r.AdoptionScore, q.SortDescending);
            }
        }

        /// <summary>
        /// The licensed-user CSV. Column order tells the story the export exists to tell: who they are,
        /// how much they are using Copilot, how that scores, and what to do about it.
        /// </summary>
        public static IReadOnlyList<CsvColumn<LicensedUserAdoptionRow>> LicensedUserColumns()
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
                new CsvColumn<LicensedUserAdoptionRow>("Copilot licences", r => r.SeatLicences),

                new CsvColumn<LicensedUserAdoptionRow>("Adoption score (0-100)", r => r.AdoptionScore),
                new CsvColumn<LicensedUserAdoptionRow>("Engagement band", r => r.BandName),
                new CsvColumn<LicensedUserAdoptionRow>("Frequency score", r => r.FrequencyScore),
                new CsvColumn<LicensedUserAdoptionRow>("Depth score", r => r.DepthScore),
                new CsvColumn<LicensedUserAdoptionRow>("Breadth score", r => r.BreadthScore),

                new CsvColumn<LicensedUserAdoptionRow>("Interactions in period", r => r.Interactions),
                new CsvColumn<LicensedUserAdoptionRow>("Active days in period", r => r.ActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Active days for full marks", r => r.ExpectedActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Copilot apps used", r => r.AppsUsed),
                new CsvColumn<LicensedUserAdoptionRow>("Copilot agents used", r => r.AgentsUsed),
                new CsvColumn<LicensedUserAdoptionRow>("Used Cowork", r => r.UsedCowork),
                new CsvColumn<LicensedUserAdoptionRow>("Cowork interactions", r => r.CoworkInteractions),

                new CsvColumn<LicensedUserAdoptionRow>("First use (UTC)", r => r.FirstInteractionUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Last use (UTC)", r => r.LastInteractionUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Days since last use", r => r.DaysSinceLastUse),

                new CsvColumn<LicensedUserAdoptionRow>("Microsoft report prompts", r => r.ReportPrompts),
                new CsvColumn<LicensedUserAdoptionRow>("Microsoft report active days", r => r.ReportActiveDays),
                new CsvColumn<LicensedUserAdoptionRow>("Microsoft report last activity", r => r.ReportLastActivityUtc),
                new CsvColumn<LicensedUserAdoptionRow>("Signal source", r => r.SignalSource),

                new CsvColumn<LicensedUserAdoptionRow>("Recommended action", r => r.RecommendedAction),
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
                default:
                    return OrderThenUpn(rows, r => r.OpportunityScore, q.SortDescending);
            }
        }

        /// <summary>
        /// The licence-opportunity CSV. Leads with the evidence (existing unlicensed Copilot use) and
        /// ends with a ready-written justification, so the file can go straight to whoever signs off
        /// the spend.
        /// </summary>
        public static IReadOnlyList<CsvColumn<LicenceOpportunityRow>> LicenceOpportunityColumns()
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

                new CsvColumn<LicenceOpportunityRow>("Unlicensed Copilot interactions", r => r.UnlicensedCopilotInteractions),
                new CsvColumn<LicenceOpportunityRow>("Unlicensed Copilot active days", r => r.UnlicensedCopilotActiveDays),
                new CsvColumn<LicenceOpportunityRow>("Last Copilot use (UTC)", r => r.LastCopilotInteractionUtc),

                new CsvColumn<LicenceOpportunityRow>("Teams messages", r => r.TeamsMessages),
                new CsvColumn<LicenceOpportunityRow>("Teams meetings", r => r.TeamsMeetings),
                new CsvColumn<LicenceOpportunityRow>("Emails sent", r => r.EmailsSent),
                new CsvColumn<LicenceOpportunityRow>("Emails read", r => r.EmailsRead),
                new CsvColumn<LicenceOpportunityRow>("Files viewed or edited", r => r.FilesViewedOrEdited),
                new CsvColumn<LicenceOpportunityRow>("Last Microsoft 365 activity", r => r.LastM365ActivityUtc),

                new CsvColumn<LicenceOpportunityRow>("Copilot demand score", r => r.CopilotDemandScore),
                new CsvColumn<LicenceOpportunityRow>("Collaboration score", r => r.CollaborationScore),
                new CsvColumn<LicenceOpportunityRow>("Email score", r => r.EmailScore),
                new CsvColumn<LicenceOpportunityRow>("Document score", r => r.DocumentScore),

                new CsvColumn<LicenceOpportunityRow>("Justification", r => r.Rationale),
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

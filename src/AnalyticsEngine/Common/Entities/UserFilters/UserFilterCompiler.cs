using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>
    /// A user filter resolved against a directory snapshot, ready to answer "is this person in?" for any
    /// user id in constant time.
    /// </summary>
    /// <remarks>
    /// Every person in the snapshot is evaluated once, up front, into a flag per row. A report then
    /// narrows its rows with a dictionary lookup and an array read per row, however many clauses the
    /// filter has - which matters when the same filter narrows five populations of up to 200,000 rows
    /// on every request.
    /// </remarks>
    public sealed class CompiledUserFilter
    {
        private readonly UserDirectorySnapshot _snapshot;
        private readonly bool[] _matchedRows;

        internal CompiledUserFilter(
            UserFilterExpression expression,
            UserDirectorySnapshot snapshot,
            bool[] matchedRows,
            IReadOnlyList<string> unknownDimensions)
        {
            Expression = expression;
            _snapshot = snapshot;
            _matchedRows = matchedRows;
            UnknownDimensions = unknownDimensions;
            MatchedPeople = matchedRows.Count(m => m);
        }

        public UserFilterExpression Expression { get; }

        /// <summary>How many people in the directory match - whether or not a given report lists them.</summary>
        public int MatchedPeople { get; }

        /// <summary>How many people the directory holds.</summary>
        public int DirectoryPeople => _snapshot.PeopleCount;

        /// <summary>When the directory the filter was resolved against was read.</summary>
        public DateTime DirectoryLoadedUtc => _snapshot.LoadedUtc;

        /// <summary>
        /// Dimensions the filter refers to that do not exist in the directory - a custom organisation type
        /// deleted or disabled after the filter was built. A condition on one matches nobody.
        /// </summary>
        public IReadOnlyList<string> UnknownDimensions { get; }

        /// <summary>
        /// Whether a person matches the filter.
        /// </summary>
        /// <remarks>
        /// A user id the snapshot does not hold - somebody imported after it was read - matches nothing.
        /// Nothing is known about them, so no condition can be shown to hold: treating them as having no
        /// values would put a Sales user imported a minute ago into "Department is not Sales". Excluding
        /// them also keeps the report's population the one <see cref="MatchedPeople"/> counts. They are
        /// picked up when the directory is next read.
        /// </remarks>
        public bool Matches(int userId)
        {
            return _snapshot.TryGetRow(userId, out var row) && _matchedRows[row];
        }

        /// <summary>
        /// A person's email domain as the directory derived it - with the same
        /// <c>CopilotAdoptionEmailDomain.From</c> a report's rows use - or <c>null</c> when they have none or
        /// the directory does not hold them.
        /// </summary>
        /// <remarks>
        /// For a person a report holds no row for, so the report's email-domain axis can be applied to them
        /// as well as the filter.
        /// </remarks>
        public string EmailDomainOf(int userId)
        {
            var column = _snapshot.Column(UserFilterDimensions.EmailDomain);
            if (column?.ValueByRow == null || !_snapshot.TryGetRow(userId, out var row)) return null;

            var index = column.ValueByRow[row];
            return index < 0 ? null : column.Values[index];
        }

        /// <summary>
        /// The display name of a dimension for the server's English artefacts: the admin's name for a
        /// custom organisation type, the English name of an Entra attribute.
        /// </summary>
        public string EnglishDimensionName(string key)
        {
            if (UserFilterDimensions.IsEntra(key)) return UserFilterDimensions.EnglishName(key);
            return _snapshot.DimensionName(key) ?? key;
        }

        /// <summary>The filter in plain English, for the Excel workbook and CSV exports.</summary>
        public string DescribeInEnglish()
        {
            return UserFilterDescriber.Describe(Expression, EnglishDimensionName);
        }

        /// <summary>What the API echoes back, so the page can say which population it is showing.</summary>
        public UserFilterEcho ToEcho()
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in Expression.Dimensions)
            {
                var name = UserFilterDimensions.IsEntra(key) ? null : _snapshot.DimensionName(key);
                if (name != null) names[key] = name;
            }

            return new UserFilterEcho
            {
                Clauses = Expression.Clauses.Select((c, i) => new UserFilterClauseModel
                {
                    Join = i == 0 ? UserFilterCodec.JoinToken(UserFilterJoin.And) : UserFilterCodec.JoinToken(c.Join),
                    Dimension = c.Dimension,
                    Operator = UserFilterCodec.OperatorToken(c.Operator),
                    Values = c.Values.ToList(),
                    IncludeNotSet = c.IncludeNotSet,
                }).ToList(),
                MatchedPeople = MatchedPeople,
                DirectoryPeople = DirectoryPeople,
                UnknownDimensions = UnknownDimensions.ToList(),
                DimensionNames = names,
            };
        }
    }

    /// <summary>Resolves a <see cref="UserFilterExpression"/> against a <see cref="UserDirectorySnapshot"/>.</summary>
    public static class UserFilterCompiler
    {
        /// <summary>
        /// Evaluates the filter for every person in the snapshot. <c>null</c> for an empty filter, which
        /// narrows nothing.
        /// </summary>
        public static CompiledUserFilter Compile(UserFilterExpression expression, UserDirectorySnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (expression == null || expression.IsEmpty) return null;

            var count = snapshot.PeopleCount;
            var unknown = new List<string>();
            var evaluated = new Dictionary<UserFilterClause, ClauseResult>();

            foreach (var clause in expression.Clauses)
            {
                evaluated[clause] = Evaluate(clause, snapshot, unknown);
            }

            var matched = new bool[count];

            foreach (var group in expression.Groups)
            {
                var results = group.Select(c => evaluated[c]).ToArray();

                for (var row = 0; row < count; row++)
                {
                    if (matched[row]) continue;

                    var all = true;
                    for (var i = 0; i < results.Length && all; i++) all = results[i].Rows[row];
                    matched[row] = all;
                }
            }

            return new CompiledUserFilter(expression, snapshot, matched, unknown.AsReadOnly());
        }

        private static ClauseResult Evaluate(UserFilterClause clause, UserDirectorySnapshot snapshot, List<string> unknown)
        {
            var count = snapshot.PeopleCount;
            var positive = new bool[count];
            var column = snapshot.Column(clause.Dimension);

            if (column == null)
            {
                // A dimension that no longer exists - a custom organisation type deleted or disabled
                // after the filter was built. A condition on it matches NOBODY, whatever its operator:
                // "Cost centre is not CC-100" on a type that has gone cannot say who was in CC-100, and
                // answering "everyone" would quietly put back the very people the filter excluded.
                // An empty result is the failure a reader notices; a widened one is not. Reported, not
                // rejected, so a link saved before the change still opens and says why.
                if (!unknown.Contains(clause.Dimension)) unknown.Add(clause.Dimension);
                return new ClauseResult { Rows = positive };
            }

            if (string.Equals(clause.Dimension, UserFilterDimensions.ManagementChain, StringComparison.Ordinal))
            {
                var managers = clause.Values.Select(column.IndexOf).Where(i => i >= 0).Distinct().ToList();
                var reporting = snapshot.RowsReportingTo(managers);
                for (var row = 0; row < count; row++)
                {
                    positive[row] = reporting[row] || (clause.IncludeNotSet && !snapshot.HasManager(row));
                }
            }
            else
            {
                var selected = SelectedValues(clause, column);
                var byRow = column.ValueByRow;
                for (var row = 0; row < count; row++)
                {
                    var value = byRow[row];
                    positive[row] = value >= 0 ? selected[value] : clause.IncludeNotSet;
                }
            }

            if (clause.IsNegated)
            {
                for (var row = 0; row < count; row++) positive[row] = !positive[row];
            }

            return new ClauseResult { Rows = positive };
        }

        /// <summary>
        /// Which of a column's distinct values the clause names. Worked out once per clause over the
        /// distinct values - a few hundred strings for most attributes, one per person for the user
        /// name - rather than once per person.
        /// </summary>
        /// <remarks>
        /// A "User name contains" condition therefore searches 200,000 strings per term on a tenant that
        /// size - measured at roughly 200 ms for two terms on a development machine, the dominant cost of
        /// compiling such a filter. A hand-written case-folding search was tried and measured no faster
        /// than <c>IndexOf(OrdinalIgnoreCase)</c>, so the framework's is kept.
        /// </remarks>
        private static bool[] SelectedValues(UserFilterClause clause, UserDirectoryColumn column)
        {
            var selected = new bool[column.Values.Count];

            if (clause.IsTextMatch)
            {
                for (var i = 0; i < column.Values.Count; i++)
                {
                    var value = column.Values[i];
                    foreach (var term in clause.Values)
                    {
                        if (value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            selected[i] = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                foreach (var value in clause.Values)
                {
                    var index = column.IndexOf(value);
                    if (index >= 0) selected[index] = true;
                }
            }

            return selected;
        }

        private sealed class ClauseResult
        {
            public bool[] Rows;
        }
    }
}

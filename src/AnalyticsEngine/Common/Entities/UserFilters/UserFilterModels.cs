using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>How a clause compares a person's value with the values the clause names.</summary>
    public enum UserFilterOperator
    {
        /// <summary>The person's value is one of the clause's values.</summary>
        Is = 1,

        /// <summary>The person's value is none of the clause's values. A person with no value matches.</summary>
        IsNot = 2,

        /// <summary>The person's value contains any of the clause's terms, case-insensitively.</summary>
        Contains = 3,

        /// <summary>The person's value contains none of the clause's terms. A person with no value matches.</summary>
        NotContains = 4,
    }

    /// <summary>How a clause combines with the clause before it.</summary>
    /// <remarks>
    /// AND binds tighter than OR, exactly as in SQL, KQL and Excel: <c>A and B or C</c> means
    /// <c>(A and B) or C</c>. That makes every filter a list of AND-groups joined by OR, which is also
    /// how it is read back to the user - "people who match any of these groups" - so the precedence is
    /// never something the reader has to know.
    /// </remarks>
    public enum UserFilterJoin
    {
        And = 1,
        Or = 2,
    }

    /// <summary>
    /// One condition on one user attribute, e.g. "Department is Sales or Marketing".
    /// </summary>
    /// <remarks>
    /// Immutable, and only ever built by <see cref="UserFilterCodec"/> (or a test), which is where the
    /// values are validated and normalised. Everything downstream can rely on a clause being well formed.
    /// </remarks>
    public sealed class UserFilterClause
    {
        public UserFilterClause(
            UserFilterJoin join,
            string dimension,
            UserFilterOperator @operator,
            IEnumerable<string> values,
            bool includeNotSet)
        {
            if (string.IsNullOrWhiteSpace(dimension)) throw new ArgumentException("A clause needs a dimension.", nameof(dimension));

            Join = join;
            Dimension = dimension;
            Operator = @operator;
            Values = (values ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
            IncludeNotSet = includeNotSet;
        }

        /// <summary>How this clause combines with the one before it. Ignored on the first clause.</summary>
        public UserFilterJoin Join { get; }

        /// <summary>A key from <see cref="UserFilterDimensions"/>, e.g. <c>department</c> or <c>org:12</c>.</summary>
        public string Dimension { get; }

        public UserFilterOperator Operator { get; }

        /// <summary>
        /// The values to compare with - or, for <see cref="UserFilterOperator.Contains"/> and
        /// <see cref="UserFilterOperator.NotContains"/>, the terms to look for. Trimmed, de-duplicated
        /// case-insensitively, never blank.
        /// </summary>
        public IReadOnlyList<string> Values { get; }

        /// <summary>
        /// Whether "has no value at all" counts as one of the clause's values.
        /// </summary>
        /// <remarks>
        /// A flag rather than a magic value in <see cref="Values"/>, because every string is a legal
        /// department or organisation name - an admin can perfectly well call a cost centre "(not set)" -
        /// and a sentinel would make that value impossible to filter on.
        /// </remarks>
        public bool IncludeNotSet { get; }

        /// <summary>True for the two operators that match what the positive form does not.</summary>
        public bool IsNegated => Operator == UserFilterOperator.IsNot || Operator == UserFilterOperator.NotContains;

        /// <summary>True for the two operators that match on part of a value.</summary>
        public bool IsTextMatch => Operator == UserFilterOperator.Contains || Operator == UserFilterOperator.NotContains;
    }

    /// <summary>
    /// A complete user filter: clauses joined by AND and OR.
    /// </summary>
    /// <remarks>
    /// Kept as the flat list the user built rather than as a tree, because that is what the UI shows and
    /// what is echoed back to it. <see cref="Groups"/> gives the evaluation shape - AND-groups joined by
    /// OR - which is derived, never stored.
    /// </remarks>
    public sealed class UserFilterExpression
    {
        public static readonly UserFilterExpression Empty = new UserFilterExpression(Enumerable.Empty<UserFilterClause>());

        public UserFilterExpression(IEnumerable<UserFilterClause> clauses)
        {
            Clauses = (clauses ?? Enumerable.Empty<UserFilterClause>()).ToList().AsReadOnly();
            Groups = BuildGroups(Clauses);
        }

        public IReadOnlyList<UserFilterClause> Clauses { get; }

        /// <summary>True when there is nothing to filter on - the whole population.</summary>
        public bool IsEmpty => Clauses.Count == 0;

        /// <summary>
        /// The clauses as they are evaluated: each inner list is an AND-group, and a person matches the
        /// filter when they match every clause of at least one group.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<UserFilterClause>> Groups { get; }

        /// <summary>Every distinct dimension the filter refers to, in the order first used.</summary>
        public IEnumerable<string> Dimensions => Clauses.Select(c => c.Dimension).Distinct(StringComparer.Ordinal);

        private static IReadOnlyList<IReadOnlyList<UserFilterClause>> BuildGroups(IReadOnlyList<UserFilterClause> clauses)
        {
            var groups = new List<IReadOnlyList<UserFilterClause>>();
            List<UserFilterClause> current = null;

            for (var i = 0; i < clauses.Count; i++)
            {
                // The first clause always opens a group, whatever join it carries: there is nothing
                // before it to join to, and a stray "or" there must not create an empty group that
                // matches nobody - or, worse, everybody.
                if (current == null || clauses[i].Join == UserFilterJoin.Or)
                {
                    current = new List<UserFilterClause>();
                    groups.Add(current.AsReadOnly());
                }

                current.Add(clauses[i]);
            }

            return groups.AsReadOnly();
        }
    }
}

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>
    /// One clause of an applied filter, as the API echoes it back.
    /// </summary>
    /// <remarks>
    /// Facts, not sentences: the dimension key, the operator token and the values. The portal writes the
    /// sentence in the reader's language - the API reports facts and the UI writes the words.
    /// </remarks>
    public sealed class UserFilterClauseModel
    {
        /// <summary><c>and</c> or <c>or</c>: how this clause joins the one before it. Always <c>and</c> on the first.</summary>
        [JsonProperty("join")]
        public string Join { get; set; }

        [JsonProperty("dimension")]
        public string Dimension { get; set; }

        /// <summary><c>is</c>, <c>isNot</c>, <c>contains</c> or <c>notContains</c>.</summary>
        [JsonProperty("operator")]
        public string Operator { get; set; }

        [JsonProperty("values")]
        public List<string> Values { get; set; } = new List<string>();

        [JsonProperty("includeNotSet")]
        public bool IncludeNotSet { get; set; }
    }

    /// <summary>
    /// The user filter a response was actually narrowed by, echoed back rather than assumed from the
    /// request - the same reason the email-domain scope is echoed. A page that is silently showing a
    /// different population from the one it names is how a licence decision goes wrong.
    /// </summary>
    public sealed class UserFilterEcho
    {
        /// <summary>The clauses, normalised - trimmed, de-duplicated, domains lower-cased.</summary>
        [JsonProperty("clauses")]
        public List<UserFilterClauseModel> Clauses { get; set; } = new List<UserFilterClauseModel>();

        /// <summary>How many people in the whole directory match, whether or not this report lists them.</summary>
        [JsonProperty("matchedPeople")]
        public int MatchedPeople { get; set; }

        /// <summary>How many people the directory holds.</summary>
        [JsonProperty("directoryPeople")]
        public int DirectoryPeople { get; set; }

        /// <summary>
        /// Dimensions the filter names that no longer exist - an organisation type deleted or disabled
        /// since the filter was built. A condition on one matches nobody, whatever its operator.
        /// </summary>
        [JsonProperty("unknownDimensions")]
        public List<string> UnknownDimensions { get; set; } = new List<string>();

        /// <summary>
        /// The admin's names for the custom organisation types the filter uses, keyed by dimension, so the
        /// page can describe the filter without a second request.
        /// </summary>
        [JsonProperty("dimensionNames")]
        public Dictionary<string, string> DimensionNames { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Writes a filter as one line of plain English, for the server's own English artefacts - the Excel
    /// workbook and the CSV exports. The portal has its own, translated, description.
    /// </summary>
    public static class UserFilterDescriber
    {
        /// <summary>Shown in place of a value for "has no value".</summary>
        public const string NotSetLabel = "not set";

        /// <summary>
        /// E.g. <c>Department is Sales or Marketing and Country or region is United Kingdom</c>, or, with
        /// OR groups, <c>(Department is Sales and Company is Contoso) or (Cost centre is CC-12)</c>.
        /// </summary>
        public static string Describe(UserFilterExpression expression, Func<string, string> dimensionName)
        {
            if (expression == null || expression.IsEmpty) return "Everyone";

            var name = dimensionName ?? UserFilterDimensions.EnglishName;
            var groups = expression.Groups
                .Select(g => string.Join(" and ", g.Select(c => DescribeClause(c, name(c.Dimension)))))
                .ToList();

            return groups.Count == 1
                ? groups[0]
                : string.Join(" or ", groups.Select(g => "(" + g + ")"));
        }

        /// <summary>
        /// One condition as a phrase, e.g. <c>Department is Sales or Marketing</c>. Worded so it never
        /// reads as a double negative: "is not" combined with "not set" becomes "is set and is not".
        /// The portal's translated description follows the same rules.
        /// </summary>
        public static string DescribeClause(UserFilterClause clause, string dimensionName)
        {
            var values = clause.Values
                .Select(v => clause.IsTextMatch
                    ? "\"" + v + "\""
                    : UserFilterDimensions.HasFixedValues(clause.Dimension) ? UserFilterTokens.EnglishName(v) : v)
                .ToList();

            if (string.Equals(clause.Dimension, UserFilterDimensions.ManagementChain, StringComparison.Ordinal))
            {
                if (clause.IncludeNotSet) values.Add("(no manager)");
                return dimensionName + (clause.IsNegated ? " does not include " : " includes ") + JoinWithOr(values);
            }

            var list = JoinWithOr(values);

            switch (clause.Operator)
            {
                case UserFilterOperator.IsNot:
                    if (values.Count == 0) return dimensionName + " is set";
                    return clause.IncludeNotSet
                        ? dimensionName + " is set and is not " + list
                        : dimensionName + " is not " + list;

                case UserFilterOperator.Contains:
                    return dimensionName + " contains " + list + (clause.IncludeNotSet ? " or is not set" : string.Empty);

                case UserFilterOperator.NotContains:
                    return clause.IncludeNotSet
                        ? dimensionName + " is set and does not contain " + list
                        : dimensionName + " does not contain " + list;

                default:
                    if (values.Count == 0) return dimensionName + " is not set";
                    return dimensionName + " is " + list + (clause.IncludeNotSet ? " or " + NotSetLabel : string.Empty);
            }
        }

        private static string JoinWithOr(IReadOnlyList<string> values)
        {
            if (values.Count == 0) return string.Empty;
            if (values.Count == 1) return values[0];
            return string.Join(", ", values.Take(values.Count - 1)) + " or " + values[values.Count - 1];
        }
    }
}

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>One condition of the global filter, as the API reports it. Facts, not sentences.</summary>
    public sealed class GlobalFilterClauseModel
    {
        /// <summary><c>and</c> or <c>or</c>: how this condition joins the one before it. Always <c>and</c> on the first.</summary>
        [JsonProperty("join")]
        public string Join { get; set; }

        [JsonProperty("dimension")]
        public string Dimension { get; set; }

        /// <summary><c>is</c>, <c>isNot</c>, <c>contains</c> or <c>notContains</c>.</summary>
        [JsonProperty("operator")]
        public string Operator { get; set; }

        /// <summary>The fixed values the administrator chose. Tenant data, never translated.</summary>
        [JsonProperty("values")]
        public List<string> Values { get; set; } = new List<string>();

        [JsonProperty("includeNotSet")]
        public bool IncludeNotSet { get; set; }

        /// <summary>The viewer attribute whose value the condition also matches, or <c>null</c>.</summary>
        [JsonProperty("viewerAttribute")]
        public string ViewerAttribute { get; set; }

        /// <summary>
        /// The signed-in person's own value for <see cref="ViewerAttribute"/> - their department, their own
        /// sign-in name - when the condition uses one and they hold it. Only ever the caller's own value.
        /// </summary>
        [JsonProperty("viewerValue")]
        public string ViewerValue { get; set; }

        /// <summary>True when the condition needed a value the signed-in person does not have, so it matches nobody.</summary>
        [JsonProperty("unresolved")]
        public bool Unresolved { get; set; }

        /// <summary>
        /// How many of the administrator's fixed values were withheld because they name people - sign-in names
        /// on a user-name, manager or management-chain condition - and the caller lacks See PII. Zero otherwise.
        /// </summary>
        [JsonProperty("hiddenValues")]
        public int HiddenValues { get; set; }

        /// <summary>
        /// True when <see cref="ViewerValue"/> was withheld for the same reason: the condition resolved for the
        /// caller (it is not <see cref="Unresolved"/>), but the value it resolved to is a person's sign-in name.
        /// </summary>
        [JsonProperty("viewerValueHidden")]
        public bool ViewerValueHidden { get; set; }

        /// <summary>A copy, with its own list of values.</summary>
        internal GlobalFilterClauseModel Copy()
        {
            var copy = (GlobalFilterClauseModel)MemberwiseClone();
            copy.Values = new List<string>(Values ?? new List<string>());
            return copy;
        }
    }

    /// <summary>
    /// The global filter as it applies to the signed-in person, echoed with a report - or on its own by
    /// <c>api/GlobalFilter/effective</c> - so the page shows the conditions that were actually applied.
    /// </summary>
    public sealed class GlobalFilterEcho
    {
        [JsonProperty("clauses")]
        public List<GlobalFilterClauseModel> Clauses { get; set; } = new List<GlobalFilterClauseModel>();

        /// <summary>How many people in the whole directory the filter leaves the signed-in person seeing.</summary>
        [JsonProperty("matchedPeople")]
        public int MatchedPeople { get; set; }

        /// <summary>How many people the directory holds.</summary>
        [JsonProperty("directoryPeople")]
        public int DirectoryPeople { get; set; }

        /// <summary>Whether the directory holds the signed-in person - false makes every viewer condition match nobody.</summary>
        [JsonProperty("viewerFound")]
        public bool ViewerFound { get; set; }

        /// <summary>Custom organisation types the filter names that no longer exist. A condition on one matches nobody.</summary>
        [JsonProperty("unknownDimensions")]
        public List<string> UnknownDimensions { get; set; } = new List<string>();

        /// <summary>The administrator's names for the custom organisation types the filter uses, keyed by dimension.</summary>
        [JsonProperty("dimensionNames")]
        public Dictionary<string, string> DimensionNames { get; set; } = new Dictionary<string, string>();

        /// <summary>Builds the echo for a resolved filter and the compiled form it was evaluated as.</summary>
        public static GlobalFilterEcho From(ResolvedGlobalFilter resolved, CompiledUserFilter compiled, UserDirectorySnapshot snapshot)
        {
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var echo = new GlobalFilterEcho
            {
                Clauses = ToModels(resolved),
                MatchedPeople = compiled?.MatchedPeople ?? snapshot.PeopleCount,
                DirectoryPeople = snapshot.PeopleCount,
                ViewerFound = resolved.ViewerFound,
                UnknownDimensions = compiled?.UnknownDimensions.ToList() ?? new List<string>(),
            };

            foreach (var key in resolved.Definition.Clauses
                .SelectMany(c => new[] { c.Clause.Dimension, c.ViewerAttribute })
                .Where(k => k != null && !UserFilterDimensions.IsEntra(k))
                .Distinct(StringComparer.Ordinal))
            {
                var name = snapshot.DimensionName(key);
                if (name != null) echo.DimensionNames[key] = name;
            }

            return echo;
        }

        /// <summary>The conditions as models, each with the viewer's value it resolved to.</summary>
        public static List<GlobalFilterClauseModel> ToModels(ResolvedGlobalFilter resolved)
        {
            return resolved.Clauses.Select((c, i) => new GlobalFilterClauseModel
            {
                Join = i == 0 ? UserFilterCodec.JoinToken(UserFilterJoin.And) : UserFilterCodec.JoinToken(c.Source.Clause.Join),
                Dimension = c.Source.Clause.Dimension,
                Operator = UserFilterCodec.OperatorToken(c.Source.Clause.Operator),
                Values = c.Source.Clause.Values.ToList(),
                IncludeNotSet = c.Source.Clause.IncludeNotSet,
                ViewerAttribute = c.Source.ViewerAttribute,
                ViewerValue = c.ViewerValue,
                Unresolved = c.Unresolved,
            }).ToList();
        }

        /// <summary>The conditions of a definition as models, with no viewer values - for the administrator's editor.</summary>
        public static List<GlobalFilterClauseModel> ToModels(GlobalFilterDefinition definition)
        {
            return (definition ?? GlobalFilterDefinition.Empty).Clauses.Select((c, i) => new GlobalFilterClauseModel
            {
                Join = i == 0 ? UserFilterCodec.JoinToken(UserFilterJoin.And) : UserFilterCodec.JoinToken(c.Clause.Join),
                Dimension = c.Clause.Dimension,
                Operator = UserFilterCodec.OperatorToken(c.Clause.Operator),
                Values = c.Clause.Values.ToList(),
                IncludeNotSet = c.Clause.IncludeNotSet,
                ViewerAttribute = c.ViewerAttribute,
            }).ToList();
        }

        /// <summary>
        /// Whether a dimension's values are people: sign-in names. A reader without See PII is never shown
        /// them - the same rule as the rest of the portal (#661, #680).
        /// </summary>
        public static bool NamesPeople(string dimension)
        {
            return string.Equals(dimension, UserFilterDimensions.UserName, StringComparison.Ordinal)
                || string.Equals(dimension, UserFilterDimensions.Manager, StringComparison.Ordinal)
                || string.Equals(dimension, UserFilterDimensions.ManagementChain, StringComparison.Ordinal);
        }

        /// <summary>
        /// This echo without the values that name people, for a caller who lacks See PII: an administrator's
        /// fixed sign-in names become a count, and a viewer value that is a sign-in name - the caller's own, or
        /// their manager's - is withheld but marked as resolved. Never modifies this instance.
        /// </summary>
        public GlobalFilterEcho WithoutPeople()
        {
            var copy = (GlobalFilterEcho)MemberwiseClone();
            copy.Clauses = Clauses.Select(c =>
            {
                var clause = c.Copy();
                if (NamesPeople(clause.Dimension) && clause.Values.Count > 0)
                {
                    clause.HiddenValues = clause.Values.Count;
                    clause.Values = new List<string>();
                }

                if (clause.ViewerValue != null && clause.ViewerAttribute != null && NamesPeople(clause.ViewerAttribute))
                {
                    clause.ViewerValue = null;
                    clause.ViewerValueHidden = true;
                }

                return clause;
            }).ToList();
            return copy;
        }
    }

    /// <summary>
    /// Writes the global filter as one line of plain English, for the server's own English artefacts -
    /// the Excel workbooks. The portal has its own, translated, description.
    /// </summary>
    public static class GlobalFilterDescriber
    {
        /// <param name="hidePeople">
        /// True for a reader without See PII: sign-in names - an administrator's chosen people, or the viewer's own
        /// or their manager's - are not written out (<see cref="GlobalFilterEcho.NamesPeople"/>).
        /// </param>
        public static string Describe(ResolvedGlobalFilter resolved, Func<string, string> dimensionName, bool hidePeople = false)
        {
            if (resolved == null || resolved.Definition.IsEmpty) return "Everyone";

            var name = dimensionName ?? UserFilterDimensions.EnglishName;
            var groups = new List<List<string>>();
            foreach (var clause in resolved.Clauses)
            {
                if (groups.Count == 0 || clause.Source.Clause.Join == UserFilterJoin.Or) groups.Add(new List<string>());

                var dimension = name(clause.Source.Clause.Dimension);
                groups[groups.Count - 1].Add(clause.Unresolved
                    ? dimension + " is the viewer's own " + name(clause.Source.ViewerAttribute) + ", which the directory does not hold for them (matches nobody)"
                    : hidePeople && GlobalFilterEcho.NamesPeople(clause.Source.Clause.Dimension)
                        ? DescribeWithoutPeople(clause, dimension)
                        : UserFilterDescriber.DescribeClause(clause.Effective, dimension));
            }

            var parts = groups.Select(g => string.Join(" and ", g)).ToList();
            return parts.Count == 1 ? parts[0] : string.Join(" or ", parts.Select(p => "(" + p + ")"));
        }

        /// <summary>A condition on people, with the people it names counted rather than listed.</summary>
        private static string DescribeWithoutPeople(ResolvedGlobalFilterClause resolved, string dimension)
        {
            var clause = resolved.Source.Clause;
            var named = clause.Values.Count;
            var parts = new List<string>();

            if (named > 0)
            {
                parts.Add(clause.IsTextMatch
                    ? (named == 1 ? "a search term" : "one of " + named + " search terms")
                    : (named == 1 ? "a named person" : "one of " + named + " named people"));
            }

            if (resolved.Source.UsesViewer)
            {
                parts.Add(string.Equals(resolved.Source.ViewerAttribute, UserFilterDimensions.Manager, StringComparison.Ordinal)
                    ? "the viewer's manager"
                    : "the viewer");
            }

            if (clause.IncludeNotSet) parts.Add(string.Equals(clause.Dimension, UserFilterDimensions.ManagementChain, StringComparison.Ordinal) ? "(no manager)" : "not set");

            var list = string.Join(" or ", parts);
            if (string.Equals(clause.Dimension, UserFilterDimensions.ManagementChain, StringComparison.Ordinal))
            {
                return dimension + (clause.IsNegated ? " does not include " : " includes ") + list;
            }

            switch (clause.Operator)
            {
                case UserFilterOperator.IsNot: return dimension + " is not " + list;
                case UserFilterOperator.Contains: return dimension + " contains " + list;
                case UserFilterOperator.NotContains: return dimension + " does not contain " + list;
                default: return dimension + " is " + list;
            }
        }
    }
}

using Common.Entities.UserFilters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.Localisation
{
    /// <summary>
    /// A filter echo as one line in the reader's language, for text the server writes (the Copilot investment workbook).
    /// The C# twin of the portal's <c>describeUserFilter</c> and, from the reader's side, <c>describeGlobalFilter</c>: the
    /// same catalog keys under the same rules, so the workbook and the page describe a filter in the same words.
    /// </summary>
    /// <remarks>
    /// Values are tenant data and are written as stored; only the product's fixed tokens (member, guest, enabled,
    /// disabled) are translated, as on the page. A custom organisation type keeps its administrator's name.
    /// </remarks>
    public static class FilterWording
    {
        private const string UserFilterModule = "userFilter";
        private const string GlobalFilterModule = "globalFilter";
        private const string CommonModule = "common";
        private const string ServerPlaceholderPrefix = "common.serverPlaceholder.";
        private const string ManagementChain = "managementChain";
        private const string EmailDomain = "emailDomain";
        private const string CustomDimensionPrefix = "org:";

        /// <summary>The dimensions whose values are the product's own tokens (<c>FIXED_VALUE_TOKENS</c>).</summary>
        private static readonly HashSet<string> FixedValueDimensions = new HashSet<string>(StringComparer.Ordinal) { "userType", "accountStatus" };

        /// <summary>The reader's own filter, e.g. "Department is Sales or Marketing". Empty for no filter.</summary>
        public static string Describe(UserFilterEcho echo, PortalLanguage language)
        {
            if (echo?.Clauses == null || echo.Clauses.Count == 0) return string.Empty;
            var text = PortalCatalog.For(UserFilterModule, language);
            var groups = Groups(echo.Clauses, c => c.Join)
                .Select(group => group.Select(c => DescribeClause(text, language, c.Dimension, c.Operator, c.Values, c.IncludeNotSet,
                    Label(text, c.Dimension, echo.DimensionNames), new List<string>())).ToList())
                .ToList();
            return JoinGroups(text, groups);
        }

        /// <summary>
        /// The administrator's filter as it applies to this reader, e.g. "Department is Sales (from your profile)". Empty for
        /// no filter. A condition the reader has no value for reads as matching nobody, as the page says.
        /// </summary>
        public static string Describe(GlobalFilterEcho echo, PortalLanguage language)
        {
            if (echo?.Clauses == null || echo.Clauses.Count == 0) return string.Empty;
            var text = PortalCatalog.For(UserFilterModule, language);
            var global = PortalCatalog.For(GlobalFilterModule, language);
            var groups = Groups(echo.Clauses, c => c.Join)
                .Select(group => group.Select(c => DescribeGlobalClause(text, global, language, c, echo.DimensionNames)).ToList())
                .ToList();
            return JoinGroups(text, groups);
        }

        /// <summary>A dimension as the page labels it: the product's name for a built-in one, the administrator's for a custom one.</summary>
        public static string DimensionLabel(string dimension, string customName, PortalLanguage language)
        {
            if (string.IsNullOrEmpty(dimension)) return customName;
            var text = PortalCatalog.For(UserFilterModule, language);
            var key = "userFilter.dimension." + dimension;
            if (!dimension.StartsWith(CustomDimensionPrefix, StringComparison.Ordinal) && text.Has(key)) return text.Text(key);
            return !string.IsNullOrEmpty(customName) ? customName : dimension;
        }

        private static string DescribeGlobalClause(PortalCatalog text, PortalCatalog global, PortalLanguage language, GlobalFilterClauseModel clause,
            IDictionary<string, string> names)
        {
            var label = Label(text, clause.Dimension, names);
            var kind = ViewerKind(clause.ViewerAttribute);
            if (!string.IsNullOrEmpty(clause.ViewerAttribute) && (clause.Unresolved || (string.IsNullOrEmpty(clause.ViewerValue) && !clause.ViewerValueHidden)))
                return global.Text("globalFilter.reader.unresolved." + kind, ("dimension", label));

            var phrases = new List<string>();
            if (clause.HiddenValues > 0)
            {
                var noun = IsTextOperator(clause.Operator) ? "globalFilter.reader.hiddenTerms." : "globalFilter.reader.hiddenPeople.";
                phrases.Add(global.Text(noun + (clause.HiddenValues == 1 ? "one" : "other"), ("count", Count(clause.HiddenValues, language))));
            }
            if (!string.IsNullOrEmpty(clause.ViewerAttribute))
            {
                if (clause.ViewerValueHidden) phrases.Add(global.Text("globalFilter.reader.hidden." + kind));
                else phrases.Add(global.Text("globalFilter.reader." + kind, ("value", ValueLabel(text, clause.Dimension, clause.ViewerValue))));
            }
            return DescribeClause(text, language, clause.Dimension, clause.Operator, clause.Values, clause.IncludeNotSet, label, phrases);
        }

        /// <summary>One condition as a phrase. Worded so it never reads as a double negative, exactly as the page words it.</summary>
        private static string DescribeClause(PortalCatalog text, PortalLanguage language, string dimension, string op, IEnumerable<string> rawValues,
            bool includeNotSet, string label, List<string> extraPhrases)
        {
            var values = (rawValues ?? Enumerable.Empty<string>())
                .Select(v => IsTextOperator(op) ? text.Text("userFilter.describe.quoted", ("term", v)) : ValueLabel(text, dimension, v))
                .Concat(extraPhrases)
                .ToList();

            if (string.Equals(dimension, ManagementChain, StringComparison.Ordinal))
            {
                var chain = OrList(values.Count > 0 ? values : new List<string> { text.Text("userFilter.describe.notSet") }, language);
                return text.Text(op == "isNot" ? "userFilter.describe.chainIsNot" : "userFilter.describe.chainIs", ("dimension", label), ("values", chain));
            }

            switch (op)
            {
                case "isNot":
                    if (values.Count == 0) return text.Text("userFilter.describe.setOnly", ("dimension", label));
                    return text.Text(includeNotSet ? "userFilter.describe.isNotAndSet" : "userFilter.describe.isNot",
                        ("dimension", label), ("values", OrList(values, language)));
                case "contains":
                    return text.Text(includeNotSet ? "userFilter.describe.containsOrNotSet" : "userFilter.describe.contains",
                        ("dimension", label), ("values", OrList(values, language)));
                case "notContains":
                    return text.Text(includeNotSet ? "userFilter.describe.notContainsAndSet" : "userFilter.describe.notContains",
                        ("dimension", label), ("values", OrList(values, language)));
                default:
                    if (values.Count == 0) return text.Text("userFilter.describe.notSetOnly", ("dimension", label));
                    if (includeNotSet) values.Add(text.Text("userFilter.describe.notSet"));
                    return text.Text("userFilter.describe.is", ("dimension", label), ("values", OrList(values, language)));
            }
        }

        private static List<List<T>> Groups<T>(IEnumerable<T> clauses, Func<T, string> join)
        {
            var groups = new List<List<T>>();
            foreach (var clause in clauses)
            {
                if (groups.Count == 0 || string.Equals(join(clause), "or", StringComparison.Ordinal)) groups.Add(new List<T>());
                groups[groups.Count - 1].Add(clause);
            }
            return groups;
        }

        /// <summary>One group joined by "and"; several, each bracketed, joined by "or" - so no "or" inside a condition is misread.</summary>
        private static string JoinGroups(PortalCatalog text, List<List<string>> groups)
        {
            if (groups.Count == 0) return string.Empty;
            if (groups.Count == 1) return Join(text, groups[0], "userFilter.describe.and");
            return Join(text, groups.Select(g => text.Text("userFilter.describe.group", ("conditions", Join(text, g, "userFilter.describe.and")))).ToList(),
                "userFilter.describe.or");
        }

        private static string Join(PortalCatalog text, IReadOnlyList<string> conditions, string key)
        {
            if (conditions.Count == 0) return string.Empty;
            var result = conditions[0];
            for (var i = 1; i < conditions.Count; i++) result = text.Text(key, ("left", result), ("right", conditions[i]));
            return result;
        }

        private static string Label(PortalCatalog text, string dimension, IDictionary<string, string> names)
        {
            string name = null;
            names?.TryGetValue(dimension ?? string.Empty, out name);
            var key = "userFilter.dimension." + dimension;
            if (!string.IsNullOrEmpty(dimension) && !dimension.StartsWith(CustomDimensionPrefix, StringComparison.Ordinal) && text.Has(key)) return text.Text(key);
            if (!string.IsNullOrEmpty(name)) return name;
            return dimension != null && dimension.StartsWith(CustomDimensionPrefix, StringComparison.Ordinal) ? text.Text("userFilter.dimension.unknown") : dimension;
        }

        private static string ValueLabel(PortalCatalog text, string dimension, string value)
        {
            if (value != null && FixedValueDimensions.Contains(dimension ?? string.Empty) && text.Has("userFilter.token." + value))
                return text.Text("userFilter.token." + value);
            // As the page's serverPlaceholderText: the domain breakdown's "(no domain)" row is the server's own text, shown in
            // the reader's language; only a whole-value match to the English placeholder is replaced, never a tenant's name.
            if (value != null && string.Equals(dimension, EmailDomain, StringComparison.Ordinal))
            {
                var english = PortalCatalog.For(CommonModule, PortalLanguage.English);
                foreach (var entry in english.Entries)
                    if (entry.Key.StartsWith(ServerPlaceholderPrefix, StringComparison.Ordinal) && string.Equals(entry.Value, value, StringComparison.Ordinal))
                        return PortalCatalog.For(CommonModule, text.Language).Text(entry.Key);
            }
            return value;
        }

        private static bool IsTextOperator(string op) => op == "contains" || op == "notContains";

        private static string ViewerKind(string viewerAttribute) =>
            viewerAttribute == UserFilterDimensions.UserName ? "self" : viewerAttribute == UserFilterDimensions.Manager ? "manager" : "own";

        private static string Count(int value, PortalLanguage language) =>
            value.ToString(language == PortalLanguage.Spanish && value < 10000 ? "0" : "#,0", PortalLanguages.Culture(language));

        /// <summary>
        /// A list read as alternatives, as the page's <c>Intl.ListFormat</c> disjunction writes it: "a, b or c", and in
        /// Spanish "a, b o c" - "u" before a word that sounds like it starts with o, by the same rule ICU applies.
        /// </summary>
        internal static string OrList(IReadOnlyList<string> values, PortalLanguage language)
        {
            if (values.Count == 0) return string.Empty;
            if (values.Count == 1) return values[0];
            var last = values[values.Count - 1];
            var conjunction = language != PortalLanguage.Spanish ? " or " : SpanishUBefore(last) ? " u " : " o ";
            return string.Join(", ", values.Take(values.Count - 1)) + conjunction + last;
        }

        /// <summary>ICU's rule for Spanish "o" becoming "u": the next word starts with o, ho or 8, or is "11" ("once").</summary>
        private static bool SpanishUBefore(string word)
        {
            var lower = (word ?? string.Empty).ToLowerInvariant();
            return lower.StartsWith("o", StringComparison.Ordinal) || lower.StartsWith("ho", StringComparison.Ordinal)
                || lower.StartsWith("8", StringComparison.Ordinal) || lower == "11" || lower.StartsWith("11 ", StringComparison.Ordinal);
        }
    }
}

using Common.Entities.CopilotAdoption;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>A user filter that could not be read. The message is written for a developer or an admin.</summary>
    public sealed class UserFilterFormatException : FormatException
    {
        public UserFilterFormatException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Reads and writes the compact JSON form a user filter travels in on a query string.
    /// </summary>
    /// <remarks>
    /// <para>The wire format is an array of clauses with short property names, because it rides on
    /// GET query strings - the CSV and Excel exports are plain links, so they cannot POST a body:</para>
    /// <code>[{"d":"department","op":"is","v":["Sales","Marketing"]},{"j":"or","d":"org:12","op":"isNot","v":["CC-100"],"n":true}]</code>
    /// <list type="bullet">
    ///   <item><c>d</c> - the dimension key (<see cref="UserFilterDimensions"/>). Required.</item>
    ///   <item><c>op</c> - <c>is</c> (default), <c>isNot</c>, <c>contains</c> or <c>notContains</c>.</item>
    ///   <item><c>v</c> - the values, or the terms for the text operators.</item>
    ///   <item><c>n</c> - true when "no value" counts as one of the values.</item>
    ///   <item><c>j</c> - <c>and</c> (default) or <c>or</c>: how the clause joins the one before it.</item>
    /// </list>
    /// <para>Parsed by hand from a <see cref="JToken"/> rather than deserialised into a type, so that
    /// nothing in an untrusted query string can choose what gets constructed, and so every rejection
    /// can say exactly which clause was wrong.</para>
    /// </remarks>
    public static class UserFilterCodec
    {
        /// <summary>More clauses than any person could read back, while bounding the work per request.</summary>
        public const int MaxClauses = 25;

        /// <summary>Per clause. Beyond this, "contains" or a custom organisation is the right tool.</summary>
        public const int MaxValuesPerClause = 500;

        /// <summary>
        /// Pieces of text one "contains" / "does not contain" condition may look for.
        /// </summary>
        /// <remarks>
        /// Far below <see cref="MaxValuesPerClause"/>, because the costs differ: an exact value is one
        /// lookup, but each piece of text is searched for in every distinct value of the attribute - one
        /// per person for the user name. At 200,000 users two terms take about 200 ms to compile, so 500
        /// terms in each of 25 conditions was minutes of CPU from one request. The portal enforces the
        /// same limits (<c>userFilterModel.ts</c>).
        /// </remarks>
        public const int MaxTextTermsPerClause = 10;

        /// <summary>Pieces of text the whole filter may look for, across every condition - about a second at 200,000 users.</summary>
        public const int MaxTextTerms = 10;

        /// <summary>The widest organisation value the schema stores (<c>user_org_values.name</c>).</summary>
        public const int MaxValueLength = 848;

        /// <summary>
        /// The longest filter accepted, in characters once decoded. The portal refuses to build one whose
        /// encoded form would pass 6,000 characters, and the web app raises the host's query-string
        /// allowance to 16 KB for the endpoints that take one - so a filter reaching this limit was not
        /// built by the portal.
        /// </summary>
        public const int MaxEncodedLength = 8000;

        /// <summary>
        /// Reads a filter from its wire form. Blank means no filter.
        /// </summary>
        /// <exception cref="UserFilterFormatException">The text is not a valid filter.</exception>
        public static UserFilterExpression Parse(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return UserFilterExpression.Empty;

            if (encoded.Length > MaxEncodedLength)
            {
                throw new UserFilterFormatException(
                    $"The filter is too long ({encoded.Length:N0} characters; the limit is {MaxEncodedLength:N0}). "
                    + "Use fewer values, a 'contains' condition, or a custom organisation instead.");
            }

            JToken root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(encoded)))
                {
                    // Dates are irrelevant here and must never be reinterpreted: a value like
                    // "2026-01-01" is a department name as far as this code is concerned.
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.MaxDepth = 8;
                    root = JToken.ReadFrom(reader);
                }
            }
            catch (JsonException ex)
            {
                throw new UserFilterFormatException("The filter is not valid JSON: " + ex.Message);
            }

            if (root.Type == JTokenType.Null) return UserFilterExpression.Empty;

            if (!(root is JArray array))
            {
                throw new UserFilterFormatException("The filter must be a JSON array of clauses.");
            }

            if (array.Count > MaxClauses)
            {
                throw new UserFilterFormatException(
                    $"The filter has {array.Count} conditions; the limit is {MaxClauses}.");
            }

            var clauses = new List<UserFilterClause>(array.Count);
            for (var i = 0; i < array.Count; i++)
            {
                clauses.Add(ParseClause(array[i], i + 1));
            }

            var textTerms = clauses.Where(c => c.IsTextMatch).Sum(c => c.Values.Count);
            if (textTerms > MaxTextTerms)
            {
                throw new UserFilterFormatException(
                    $"The filter looks for {textTerms:N0} pieces of text in all; the limit is {MaxTextTerms:N0}.");
            }

            return new UserFilterExpression(clauses);
        }

        /// <summary>Writes a filter in its wire form, or <c>null</c> for an empty one.</summary>
        public static string Serialize(UserFilterExpression expression)
        {
            if (expression == null || expression.IsEmpty) return null;

            var array = new JArray();
            for (var i = 0; i < expression.Clauses.Count; i++)
            {
                var clause = expression.Clauses[i];
                var item = new JObject();

                if (i > 0 && clause.Join == UserFilterJoin.Or) item["j"] = "or";
                item["d"] = clause.Dimension;
                if (clause.Operator != UserFilterOperator.Is) item["op"] = OperatorToken(clause.Operator);
                item["v"] = new JArray(clause.Values.Cast<object>().ToArray());
                if (clause.IncludeNotSet) item["n"] = true;

                array.Add(item);
            }

            return array.ToString(Formatting.None);
        }

        /// <summary>The wire token for an operator. Also what the API echoes back.</summary>
        public static string OperatorToken(UserFilterOperator op)
        {
            switch (op)
            {
                case UserFilterOperator.IsNot: return "isNot";
                case UserFilterOperator.Contains: return "contains";
                case UserFilterOperator.NotContains: return "notContains";
                default: return "is";
            }
        }

        /// <summary>The wire token for a join. Also what the API echoes back.</summary>
        public static string JoinToken(UserFilterJoin join)
        {
            return join == UserFilterJoin.Or ? "or" : "and";
        }

        private static UserFilterClause ParseClause(JToken token, int number)
        {
            if (!(token is JObject item))
            {
                throw new UserFilterFormatException($"Condition {number} is not a JSON object.");
            }

            var dimension = ReadString(item, "d", number);
            if (string.IsNullOrWhiteSpace(dimension))
            {
                throw new UserFilterFormatException($"Condition {number} does not say which attribute it tests.");
            }

            dimension = dimension.Trim();
            if (!UserFilterDimensions.IsWellFormed(dimension))
            {
                throw new UserFilterFormatException(
                    $"Condition {number} tests '{Truncate(dimension)}', which is not an attribute this report can filter on.");
            }

            var op = ParseOperator(ReadString(item, "op", number), number);
            var join = ParseJoin(ReadString(item, "j", number), number);

            if ((op == UserFilterOperator.Contains || op == UserFilterOperator.NotContains)
                && !UserFilterDimensions.SupportsTextMatch(dimension))
            {
                throw new UserFilterFormatException(
                    $"Condition {number} uses '{OperatorToken(op)}' on '{dimension}', which can only be matched exactly.");
            }

            var includeNotSet = ReadBool(item, "n", number);
            var values = NormaliseValues(dimension, op, ReadValues(item, number), number);

            // The report lists people with no derivable domain under "(no domain)", and that row is
            // selectable. It is the absence of a value, not a domain, so it becomes the not-set flag -
            // otherwise it would be compared with real domains and match nobody.
            if (string.Equals(dimension, UserFilterDimensions.EmailDomain, StringComparison.Ordinal)
                && (op == UserFilterOperator.Is || op == UserFilterOperator.IsNot)
                && values.RemoveAll(v => string.Equals(v, CopilotAdoptionEmailDomain.NoDomainLabel, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                includeNotSet = true;
            }

            if (values.Count == 0 && !includeNotSet)
            {
                throw new UserFilterFormatException($"Condition {number} has no values to compare with.");
            }

            return new UserFilterClause(join, dimension, op, values, includeNotSet);
        }

        private static List<string> NormaliseValues(string dimension, UserFilterOperator op, IEnumerable<string> raw, int number)
        {
            var fixedValues = UserFilterDimensions.FixedValues(dimension);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();

            foreach (var candidate in raw)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                var value = candidate.Trim();
                if (value.Length > MaxValueLength)
                {
                    throw new UserFilterFormatException(
                        $"A value in condition {number} is {value.Length:N0} characters long; the limit is {MaxValueLength:N0}.");
                }

                if (fixedValues != null)
                {
                    var token = fixedValues.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase));
                    if (token == null)
                    {
                        throw new UserFilterFormatException(
                            $"Condition {number} compares '{dimension}' with '{Truncate(value)}'; the allowed values are "
                            + string.Join(", ", fixedValues) + ".");
                    }

                    value = token;
                }
                else if (string.Equals(dimension, UserFilterDimensions.EmailDomain, StringComparison.Ordinal)
                    && op != UserFilterOperator.Contains && op != UserFilterOperator.NotContains)
                {
                    // Normalised the same way the report derives a person's domain, so "@Contoso.com"
                    // typed into a URL matches the people the page lists under contoso.com.
                    value = CopilotAdoptionEmailDomain.Normalise(value) ?? value;
                }

                if (seen.Add(value)) values.Add(value);
            }

            if (values.Count > MaxValuesPerClause)
            {
                throw new UserFilterFormatException(
                    $"Condition {number} has {values.Count:N0} values; the limit is {MaxValuesPerClause:N0}. "
                    + "Use a 'contains' condition or a custom organisation instead.");
            }

            if ((op == UserFilterOperator.Contains || op == UserFilterOperator.NotContains) && values.Count > MaxTextTermsPerClause)
            {
                throw new UserFilterFormatException(
                    $"Condition {number} looks for {values.Count:N0} pieces of text; the limit is {MaxTextTermsPerClause:N0}.");
            }

            return values;
        }

        private static UserFilterOperator ParseOperator(string token, int number)
        {
            if (string.IsNullOrWhiteSpace(token)) return UserFilterOperator.Is;

            switch (token.Trim())
            {
                case "is": return UserFilterOperator.Is;
                case "isNot": return UserFilterOperator.IsNot;
                case "contains": return UserFilterOperator.Contains;
                case "notContains": return UserFilterOperator.NotContains;
                default:
                    throw new UserFilterFormatException(
                        $"Condition {number} uses the operator '{Truncate(token)}'; expected is, isNot, contains or notContains.");
            }
        }

        private static UserFilterJoin ParseJoin(string token, int number)
        {
            if (string.IsNullOrWhiteSpace(token)) return UserFilterJoin.And;

            switch (token.Trim())
            {
                case "and": return UserFilterJoin.And;
                case "or": return UserFilterJoin.Or;
                default:
                    throw new UserFilterFormatException(
                        $"Condition {number} joins with '{Truncate(token)}'; expected and or or.");
            }
        }

        private static string ReadString(JObject item, string name, int number)
        {
            var token = item[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String)
            {
                throw new UserFilterFormatException($"Condition {number}: '{name}' must be a string.");
            }

            return (string)token;
        }

        private static bool ReadBool(JObject item, string name, int number)
        {
            var token = item[name];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type != JTokenType.Boolean)
            {
                throw new UserFilterFormatException($"Condition {number}: '{name}' must be true or false.");
            }

            return (bool)token;
        }

        private static IEnumerable<string> ReadValues(JObject item, int number)
        {
            var token = item["v"];
            if (token == null || token.Type == JTokenType.Null) return Enumerable.Empty<string>();

            if (!(token is JArray array))
            {
                throw new UserFilterFormatException($"Condition {number}: 'v' must be an array of strings.");
            }

            // Counted before normalising, so a request cannot make the server trim and de-duplicate an
            // arbitrarily long list only to be told afterwards that it was too long.
            if (array.Count > MaxValuesPerClause * 2)
            {
                throw new UserFilterFormatException(
                    $"Condition {number} has {array.Count:N0} values; the limit is {MaxValuesPerClause:N0}.");
            }

            var values = new List<string>(array.Count);
            foreach (var value in array)
            {
                if (value.Type != JTokenType.String)
                {
                    throw new UserFilterFormatException($"Condition {number}: every value must be a string.");
                }

                values.Add((string)value);
            }

            return values;
        }

        private static string Truncate(string value)
        {
            const int max = 60;
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}

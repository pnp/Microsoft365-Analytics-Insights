using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>
    /// Reads and writes the administrator's global filter.
    /// </summary>
    /// <remarks>
    /// <para>The same compact form as a user filter (<see cref="UserFilterCodec"/>), with one more property
    /// per condition:</para>
    /// <list type="bullet">
    ///   <item><c>vu</c> - a viewer attribute (<see cref="GlobalFilterViewerAttributes"/>): the value the person
    ///   viewing the report holds for it is added to the condition's values. A condition with <c>vu</c> may
    ///   name no fixed values at all.</item>
    /// </list>
    /// <code>[{"d":"department","vu":"department"},{"d":"userType","v":["member"]}]</code>
    /// <para>"Department is the viewer's own department, and the person is a member rather than a guest."
    /// Every other rule - limits, normalisation, which operators suit which attribute - is the user
    /// filter's, applied by the same code, so the two can never disagree about what a condition means.</para>
    /// </remarks>
    public static class GlobalFilterCodec
    {
        /// <summary>Reads a definition. Blank means no global filter.</summary>
        /// <exception cref="UserFilterFormatException">The text is not a valid global filter.</exception>
        public static GlobalFilterDefinition Parse(string encoded)
        {
            var array = UserFilterCodec.ReadClauseArray(encoded);
            if (array == null) return GlobalFilterDefinition.Empty;

            var clauses = new List<GlobalFilterClause>(array.Count);
            for (var i = 0; i < array.Count; i++)
            {
                var number = i + 1;
                if (!(array[i] is JObject item))
                {
                    throw new UserFilterFormatException($"Condition {number} is not a JSON object.");
                }

                var viewerAttribute = ReadViewerAttribute(item, number);
                var clause = UserFilterCodec.ParseClause(item, number, allowNoValues: viewerAttribute != null);

                if (viewerAttribute != null)
                {
                    if (clause.IsTextMatch)
                    {
                        throw new UserFilterFormatException(
                            $"Condition {number} looks for text, which cannot be compared with the viewer's own value.");
                    }

                    if (!GlobalFilterViewerAttributes.IsAllowed(clause.Dimension, viewerAttribute))
                    {
                        throw new UserFilterFormatException(
                            $"Condition {number} compares '{clause.Dimension}' with the viewer's '{viewerAttribute}', which is not a comparison this filter supports.");
                    }
                }

                clauses.Add(new GlobalFilterClause(clause, viewerAttribute));
            }

            UserFilterCodec.CheckTextTerms(clauses.Select(c => c.Clause));
            return new GlobalFilterDefinition(clauses);
        }

        /// <summary>Writes a definition in its wire form, or <c>null</c> for an empty one.</summary>
        public static string Serialize(GlobalFilterDefinition definition)
        {
            if (definition == null || definition.IsEmpty) return null;

            var array = new JArray();
            for (var i = 0; i < definition.Clauses.Count; i++)
            {
                var clause = definition.Clauses[i].Clause;
                var item = new JObject();

                if (i > 0 && clause.Join == UserFilterJoin.Or) item["j"] = "or";
                item["d"] = clause.Dimension;
                if (clause.Operator != UserFilterOperator.Is) item["op"] = UserFilterCodec.OperatorToken(clause.Operator);
                item["v"] = new JArray(clause.Values.Cast<object>().ToArray());
                if (clause.IncludeNotSet) item["n"] = true;
                if (definition.Clauses[i].UsesViewer) item["vu"] = definition.Clauses[i].ViewerAttribute;

                array.Add(item);
            }

            return array.ToString(Formatting.None);
        }

        private static string ReadViewerAttribute(JObject item, int number)
        {
            var token = item["vu"];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String)
            {
                throw new UserFilterFormatException($"Condition {number}: 'vu' must be a string.");
            }

            var value = ((string)token).Trim();
            if (value.Length == 0) return null;

            if (!UserFilterDimensions.IsWellFormed(value))
            {
                throw new UserFilterFormatException(
                    $"Condition {number} compares with the viewer's '{(value.Length > 60 ? value.Substring(0, 60) + "..." : value)}', which is not an attribute a person holds.");
            }

            return value;
        }
    }
}

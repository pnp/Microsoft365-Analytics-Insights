using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Binds a <c>PolicyDetails</c> list in whichever shape it arrives, and never throws because of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AccessedResources[].PolicyDetails</c> is documented only in prose, as an array of
    /// <c>PolicyId</c> / <c>PolicyName</c> / <c>Rules</c>. Copilot audit records now also deliver it as a
    /// <b>string</b> holding a JSON array of undocumented <c>PolicyType</c> / <c>PolicyOutcomes</c> /
    /// <c>AuditLog</c> entries. A typed list cannot be bound from a string, so before this converter the
    /// whole interaction was thrown away (issue #659).
    /// </para>
    /// <para>
    /// The value is bound as follows:
    /// <list type="bullet">
    /// <item>absent or <c>null</c>: <c>null</c>, as before.</item>
    /// <item><c>""</c>: <c>null</c>, as before.</item>
    /// <item>an array: bound exactly as before. An element that cannot be bound is dropped rather than
    /// failing the record.</item>
    /// <item>a string whose content is a JSON array: parsed, then bound like an array.</item>
    /// <item>anything else (a string that is not JSON, a string holding something other than an array, an
    /// object, a number, a boolean): <c>null</c>, meaning "no policy detail".</item>
    /// </list>
    /// </para>
    /// <para>
    /// Write is not overridden (<see cref="CanWrite"/> is false), so serialising the list (the
    /// <c>accessed_resources_json</c> staging column) writes an ordinary JSON array.
    /// </para>
    /// </remarks>
    public sealed class TolerantPolicyDetailsConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(List<AccessedResourcePolicyDetail>);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = EmbeddedJson.LoadValue(reader);
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Array)
            {
                return BindEntries((JArray)token, serializer);
            }

            if (token.Type == JTokenType.String
                && EmbeddedJson.TryParse((string)token, out var parsed)
                && parsed.Type == JTokenType.Array)
            {
                return BindEntries((JArray)parsed, serializer);
            }

            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("CanWrite is false, so the default serialiser writes PolicyDetails.");
        }

        private static List<AccessedResourcePolicyDetail> BindEntries(JArray entries, JsonSerializer serializer)
        {
            var result = new List<AccessedResourcePolicyDetail>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry.Type == JTokenType.Null)
                {
                    // A null element has always bound as a null entry, and every reader skips it.
                    result.Add(null);
                    continue;
                }

                if (entry.Type != JTokenType.Object)
                {
                    continue;
                }

                try
                {
                    result.Add(entry.ToObject<AccessedResourcePolicyDetail>(serializer));
                }
                catch (Exception ex) when (EmbeddedJson.IsBindingFailure(ex))
                {
                    // One unreadable entry costs that entry, not the interaction.
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Decodes <see cref="AccessedResourcePolicyDetail.AuditLog"/>, which arrives as a string holding a JSON
    /// document (its quotes escaped as <c>\u0022</c> on the wire). Never throws because of the value.
    /// </summary>
    /// <remarks>
    /// <c>""</c>, a string that is not JSON, a string or value that is not a JSON object, and an object
    /// that cannot be bound all become <c>null</c>. A JSON object that arrives unencoded is bound too.
    /// </remarks>
    public sealed class TolerantPolicyAuditLogConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(AccessedResourcePolicyAuditLog);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = EmbeddedJson.LoadValue(reader);
            if (token != null && token.Type == JTokenType.String && !EmbeddedJson.TryParse((string)token, out token))
            {
                return null;
            }

            if (token == null || token.Type != JTokenType.Object)
            {
                return null;
            }

            try
            {
                return token.ToObject<AccessedResourcePolicyAuditLog>(serializer);
            }
            catch (Exception ex) when (EmbeddedJson.IsBindingFailure(ex))
            {
                return null;
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("CanWrite is false, so the default serialiser writes AuditLog.");
        }
    }

    /// <summary>
    /// Keeps <see cref="AccessedResourcePolicyDetail.PolicyOutcomes"/> verbatim whatever its shape: an array
    /// of values, or a single string. Anything else becomes <c>null</c>. Never throws because of the value.
    /// </summary>
    public sealed class TolerantStringListConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(List<string>);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = EmbeddedJson.LoadValue(reader);
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                return new List<string> { (string)token };
            }

            if (token.Type != JTokenType.Array)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var item in token.Children())
            {
                if (item.Type == JTokenType.Null)
                {
                    continue;
                }

                values.Add(item.Type == JTokenType.String ? (string)item : item.ToString(Formatting.None));
            }

            return values;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("CanWrite is false, so the default serialiser writes the list.");
        }
    }

    /// <summary>
    /// Helpers for values that hold JSON inside a JSON string.
    /// </summary>
    internal static class EmbeddedJson
    {
        private const string EscapedQuote = @"\u0022";

        /// <summary>
        /// Reads the value the reader is on, without turning date-like strings into dates, so a policy name
        /// such as "2026-01-01" stays the string it was sent as.
        /// </summary>
        /// <returns>The value, or null for a JSON null.</returns>
        internal static JToken LoadValue(JsonReader reader)
        {
            var previous = reader.DateParseHandling;
            reader.DateParseHandling = DateParseHandling.None;
            try
            {
                var token = JToken.Load(reader);
                return token.Type == JTokenType.Null || token.Type == JTokenType.Undefined ? null : token;
            }
            finally
            {
                reader.DateParseHandling = previous;
            }
        }

        /// <summary>
        /// Parses a string whose whole content should be one JSON document. Returns false, rather than
        /// throwing, for an empty string, a string that is not JSON, or one with trailing content.
        /// </summary>
        /// <remarks>
        /// A JSON-in-a-string value normally decodes to ordinary JSON, because the outer document's
        /// <c>\u0022</c> escapes are decoded with it. If a producer escaped the quotes twice, the decoded
        /// text still holds literal <c>\u0022</c> sequences; those are turned into quotes and the parse is
        /// retried once.
        /// </remarks>
        internal static bool TryParse(string text, out JToken token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (TryParseExact(text, out token))
            {
                return true;
            }

            return text.IndexOf(EscapedQuote, StringComparison.Ordinal) >= 0
                && TryParseExact(text.Replace(EscapedQuote, "\""), out token);
        }

        /// <summary>
        /// The exceptions binding a well-formed JSON value to a model can raise when the value has an
        /// unexpected shape or type.
        /// </summary>
        internal static bool IsBindingFailure(Exception ex)
        {
            return ex is JsonException
                || ex is ArgumentException
                || ex is FormatException
                || ex is InvalidCastException
                || ex is OverflowException;
        }

        private static bool TryParseExact(string text, out JToken token)
        {
            token = null;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    var value = JToken.ReadFrom(reader);
                    if (reader.Read())
                    {
                        return false;
                    }

                    token = value;
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}

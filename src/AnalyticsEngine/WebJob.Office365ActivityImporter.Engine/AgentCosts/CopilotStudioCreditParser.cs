using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Turns a Power Platform licensing API response into consumption rows.
    ///
    /// Pure and static so the awkward part of this import - a response shape that is only partly documented -
    /// can be tested against captured JSON without HTTP, a tenant or a database.
    /// </summary>
    /// <remarks>
    /// <para><b>Two envelopes are accepted deliberately.</b> Microsoft's REST reference documents a flat
    /// <c>value[]</c> of resource snapshots, but a live tenant queried with <c>includeFields</c> returns those
    /// same rows nested under <c>value[].resources[]</c>. Only the flat shape is documented and only the
    /// nested shape has been observed, so supporting exactly one of them would be a bet. Both are handled, and
    /// an envelope that is neither yields no rows rather than an exception - the importer records "read 0
    /// rows", which is visible in the import log, instead of failing the whole cycle.</para>
    ///
    /// <para>Field names are matched case-insensitively. The documented and observed casings already differ
    /// (<c>continuationtoken</c> vs <c>continuationToken</c>), and this is not a schema we control.</para>
    /// </remarks>
    public static class CopilotStudioCreditParser
    {
        /// <summary>
        /// Parses a consumption response. Never throws for a merely unexpected shape; a caller that passes
        /// malformed JSON will still see the <see cref="Newtonsoft.Json"/> exception from its own parse.
        /// </summary>
        public static CopilotStudioCreditPage ParseConsumptionPage(JObject response)
        {
            var rows = new List<CopilotStudioCreditRow>();
            if (response == null)
            {
                return new CopilotStudioCreditPage(rows, null);
            }

            var value = GetProperty(response, "value") as JArray;
            if (value != null)
            {
                foreach (var entry in value)
                {
                    var entryObject = entry as JObject;
                    if (entryObject == null) continue;

                    // Nested envelope: value[] holds groups, each with a resources[] array of the real rows.
                    var nested = GetProperty(entryObject, "resources") as JArray;
                    if (nested != null)
                    {
                        foreach (var nestedEntry in nested)
                        {
                            var row = ParseRow(nestedEntry as JObject);
                            if (row != null) rows.Add(row);
                        }
                        continue;
                    }

                    // Flat envelope: value[] holds the rows directly.
                    var flat = ParseRow(entryObject);
                    if (flat != null) rows.Add(flat);
                }
            }

            var continuation = GetString(response, "continuationtoken") ?? GetString(response, "continuationToken");

            return new CopilotStudioCreditPage(rows, continuation);
        }

        /// <summary>
        /// Parses the tenant entitlement response. Returns null when the payload carries no entitlement, so
        /// the caller can tell "no capacity information" from "capacity of zero".
        /// </summary>
        public static CopilotStudioCapacitySnapshot ParseCapacity(JObject response)
        {
            if (response == null) return null;

            var entitlement = GetProperty(response, "entitlement") as JObject;
            if (entitlement == null) return null;

            var capacity = GetProperty(entitlement, "capacity") as JObject;
            var payGo = GetProperty(entitlement, "payGo") as JObject;

            var snapshot = new CopilotStudioCapacitySnapshot();

            if (capacity != null)
            {
                snapshot.Entitled = GetDecimal(GetProperty(capacity, "entitled") as JObject, "value");

                var consumed = GetProperty(capacity, "consumed") as JObject;
                if (consumed != null)
                {
                    snapshot.Consumed = GetDecimal(consumed, "value");
                    snapshot.ConsumptionType = GetString(consumed, "consumptionType");
                    snapshot.ConsumedLastUpdatedOn = GetDateTime(consumed, "lastUpdatedOn");
                }

                snapshot.Allocated = GetDecimal(GetProperty(capacity, "allocated") as JObject, "value");
                snapshot.Available = GetDecimal(capacity, "availableQuantity");
                snapshot.Status = GetString(capacity, "status");
            }

            if (payGo != null)
            {
                snapshot.PayAsYouGoConsumed = GetDecimal(GetProperty(payGo, "consumed") as JObject, "value");
            }

            return snapshot;
        }

        /// <summary>
        /// Parses an environment-management response into an id =&gt; name map. Environments with no id are
        /// skipped, and a duplicate id keeps the first name seen.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ParseEnvironmentNames(JObject response)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (response == null) return map;

            var value = GetProperty(response, "value") as JArray;
            if (value == null) return map;

            foreach (var entry in value)
            {
                var environment = entry as JObject;
                if (environment == null) continue;

                var id = GetString(environment, "id") ?? GetString(environment, "name");
                if (string.IsNullOrWhiteSpace(id)) continue;

                // The display name lives on the properties object in the documented shape, and sometimes at
                // the top level. Neither is guaranteed, and a missing name is not an error.
                var properties = GetProperty(environment, "properties") as JObject;
                var displayName = (properties != null ? GetString(properties, "displayName") : null)
                    ?? GetString(environment, "displayName");

                if (!map.ContainsKey(id))
                {
                    map.Add(id, displayName);
                }
            }

            return map;
        }

        /// <summary>
        /// Parses a <b>per-user</b> consumption response.
        /// </summary>
        /// <remarks>
        /// The envelope is <c>value[].users[]</c>, not <c>value[].resources[]</c>, so this cannot reuse the
        /// per-agent parser. A flat <c>value[]</c> of user snapshots is accepted too, on the same reasoning
        /// as the per-agent read: the documented and observed shapes differ and neither is safe to assume.
        /// </remarks>
        public static CopilotStudioUserCreditPage ParseUserConsumptionPage(JObject response)
        {
            var rows = new List<CopilotStudioUserCreditRow>();
            if (response == null)
            {
                return new CopilotStudioUserCreditPage(rows, null);
            }

            var value = GetProperty(response, "value") as JArray;
            if (value != null)
            {
                foreach (var entry in value)
                {
                    var entryObject = entry as JObject;
                    if (entryObject == null) continue;

                    var nested = GetProperty(entryObject, "users") as JArray;
                    if (nested != null)
                    {
                        foreach (var nestedEntry in nested)
                        {
                            var row = ParseUserRow(nestedEntry as JObject);
                            if (row != null) rows.Add(row);
                        }
                        continue;
                    }

                    var flat = ParseUserRow(entryObject);
                    if (flat != null) rows.Add(flat);
                }
            }

            var continuation = GetString(response, "continuationtoken") ?? GetString(response, "continuationToken");

            return new CopilotStudioUserCreditPage(rows, continuation);
        }

        private static CopilotStudioUserCreditRow ParseUserRow(JObject row)
        {
            if (row == null) return null;

            var userId = GetString(row, "userId");

            // A row with no user is not a user row. Storing it under a null identifier would create a
            // phantom "unknown user" bucket that silently accumulates other people's spend.
            if (string.IsNullOrWhiteSpace(userId)) return null;

            return new CopilotStudioUserCreditRow
            {
                UserId = userId,
                EnvironmentId = GetString(row, "environmentId"),
                Consumed = GetDecimal(row, "consumed") ?? 0m,
                Unit = GetString(row, "unit"),
                AsOfDate = GetDateTime(row, "asOfDate"),
            };
        }

        private static CopilotStudioCreditRow ParseRow(JObject row)
        {
            if (row == null) return null;

            var metadata = GetProperty(row, "metadata") as JObject;

            return new CopilotStudioCreditRow
            {
                EnvironmentId = GetString(row, "environmentId"),
                ResourceId = GetString(row, "resourceId"),
                Consumed = GetDecimal(row, "consumed") ?? 0m,
                LastRefreshedDate = GetDateTime(row, "lastRefreshedDate"),

                // asOfDate is documented on neither envelope but is what a live tenant returns as the usage
                // day. The importer falls back to the requested date when it is absent.
                AsOfDate = GetDateTime(row, "asOfDate") ?? (metadata != null ? GetDateTime(metadata, "AsOfDate") : null),

                ResourceName = metadata != null ? GetString(metadata, "ResourceName") : null,
                NonBillableQuantity = metadata != null ? GetDecimal(metadata, "NonBillableQuantity") : null,
                Users = metadata != null ? GetInt(metadata, "Users") : null,
                ChannelId = metadata != null ? GetString(metadata, "ChannelId") : null,
                KnowledgeSources = metadata != null ? GetString(metadata, "KnowledgeSources") : null,
                ToolInvoked = metadata != null ? GetString(metadata, "ToolInvoked") : null,
                LlmModel = metadata != null ? GetString(metadata, "LLMModel") : null,
                FeatureName = metadata != null ? GetString(metadata, "FeatureName") : null,
            };
        }

        /// <summary>Case-insensitive property read, because the response casing is not ours to rely on.</summary>
        private static JToken GetProperty(JObject o, string name)
        {
            if (o == null) return null;
            var property = o.Property(name, StringComparison.OrdinalIgnoreCase);
            return property?.Value;
        }

        private static string GetString(JObject o, string name)
        {
            var token = GetProperty(o, name);
            if (token == null || token.Type == JTokenType.Null) return null;

            var s = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static decimal? GetDecimal(JObject o, string name)
        {
            var token = GetProperty(o, name);
            if (token == null || token.Type == JTokenType.Null) return null;

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return token.Value<decimal>();
            }

            // Numbers sometimes arrive quoted. Invariant culture, so a decimal point is never read as a
            // thousands separator on a host with a European locale.
            return decimal.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (decimal?)null;
        }

        private static int? GetInt(JObject o, string name)
        {
            var value = GetDecimal(o, name);
            if (!value.HasValue) return null;

            // Clamp rather than overflow: this is a user count used for reporting, and an absurd value from
            // the API must not throw away the billing figures on the same row.
            if (value.Value > int.MaxValue) return int.MaxValue;
            if (value.Value < int.MinValue) return int.MinValue;
            return (int)value.Value;
        }

        private static DateTime? GetDateTime(JObject o, string name)
        {
            var token = GetProperty(o, name);
            if (token == null || token.Type == JTokenType.Null) return null;

            if (token.Type == JTokenType.Date)
            {
                return NormaliseToUtc(token.Value<DateTime>());
            }

            var s = token.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;

            // AdjustToUniversal so an offset-bearing timestamp becomes UTC; AssumeUniversal so one without an
            // offset (which is what "2026-08-23T00:00:00" is) is not reinterpreted through the host's local
            // time zone. Without the latter, the same response would produce a different usage day depending
            // on where the web-job happens to run.
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? NormaliseToUtc(parsed)
                : (DateTime?)null;
        }

        private static DateTime NormaliseToUtc(DateTime value)
        {
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    return value;
                case DateTimeKind.Local:
                    return value.ToUniversalTime();
                default:
                    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }
    }
}

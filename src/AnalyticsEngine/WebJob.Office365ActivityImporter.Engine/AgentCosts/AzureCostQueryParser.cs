using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Turns a Microsoft Cost Management <c>query</c> response into daily cost rows.
    ///
    /// Pure and static so the columnar response shape can be tested against captured JSON without an Azure
    /// subscription.
    /// </summary>
    /// <remarks>
    /// <para>The response is columnar: <c>properties.columns[]</c> names the fields and
    /// <c>properties.rows[][]</c> holds parallel arrays of values. Column <b>order is not guaranteed</b> and
    /// depends on the grouping requested, so every value is read by resolving its column name to an index
    /// rather than by position - reading positionally would silently swap cost and quantity the first time
    /// Azure reorders them.</para>
    ///
    /// <para>Names vary by agreement type. The cost column is <c>PreTaxCost</c> on some agreements and
    /// <c>Cost</c> or <c>CostUSD</c> on others, so each field is looked up through a list of accepted aliases.</para>
    /// </remarks>
    public static class AzureCostQueryParser
    {
        // Aliases are ordered by preference: the billing-currency figure is what appears on the invoice, so it
        // is taken ahead of the USD-normalised one.
        private static readonly string[] CostColumns = { "PreTaxCost", "Cost", "CostInBillingCurrency", "PreTaxCostUSD", "CostUSD" };
        private static readonly string[] DateColumns = { "UsageDate", "Date", "BillingMonth" };
        private static readonly string[] CurrencyColumns = { "Currency", "BillingCurrency", "BillingCurrencyCode" };
        private static readonly string[] SubscriptionColumns = { "SubscriptionId", "SubscriptionGuid" };
        private static readonly string[] ResourceIdColumns = { "ResourceId", "InstanceId" };
        private static readonly string[] ResourceGroupColumns = { "ResourceGroup", "ResourceGroupName" };
        private static readonly string[] ServiceNameColumns = { "ServiceName", "ConsumedService", "ServiceFamily" };
        private static readonly string[] MeterCategoryColumns = { "MeterCategory", "ServiceTier" };
        private static readonly string[] MeterSubCategoryColumns = { "MeterSubCategory" };
        private static readonly string[] MeterNameColumns = { "MeterName", "Meter" };
        private static readonly string[] QuantityColumns = { "UsageQuantity", "Quantity" };

        /// <summary>
        /// Parses one page. Returns an empty list for a response with no rows - which is a legitimate answer
        /// (a filter that matched nothing) and not an error.
        /// </summary>
        public static IReadOnlyList<AzureCostRow> ParsePage(JObject response)
        {
            var results = new List<AzureCostRow>();
            if (response == null) return results;

            var properties = GetProperty(response, "properties") as JObject;

            // Some callers hand back the properties object itself; accept either.
            var container = properties ?? response;

            var columns = GetProperty(container, "columns") as JArray;
            var rows = GetProperty(container, "rows") as JArray;
            if (columns == null || rows == null) return results;

            var index = BuildColumnIndex(columns);

            foreach (var row in rows)
            {
                var cells = row as JArray;
                if (cells == null) continue;

                var usageDate = ReadDate(cells, index, DateColumns);
                if (!usageDate.HasValue)
                {
                    // Without a usage date the row cannot be placed on a timeline or de-duplicated, so it is
                    // dropped rather than stored against a guessed date.
                    continue;
                }

                results.Add(new AzureCostRow
                {
                    UsageDate = usageDate.Value,
                    Cost = ReadDecimal(cells, index, CostColumns) ?? 0m,
                    Currency = ReadString(cells, index, CurrencyColumns),
                    SubscriptionId = ReadString(cells, index, SubscriptionColumns),
                    ResourceId = ReadString(cells, index, ResourceIdColumns),
                    ResourceGroup = ReadString(cells, index, ResourceGroupColumns),
                    ServiceName = ReadString(cells, index, ServiceNameColumns),
                    MeterCategory = ReadString(cells, index, MeterCategoryColumns),
                    MeterSubCategory = ReadString(cells, index, MeterSubCategoryColumns),
                    MeterName = ReadString(cells, index, MeterNameColumns),
                    Quantity = ReadDecimal(cells, index, QuantityColumns),
                });
            }

            return results;
        }

        /// <summary>The URL of the next page, or null when this was the last one.</summary>
        public static string GetNextLink(JObject response)
        {
            if (response == null) return null;

            var properties = GetProperty(response, "properties") as JObject;
            var container = properties ?? response;

            var value = GetString(container, "nextLink") ?? GetString(response, "nextLink");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static Dictionary<string, int> BuildColumnIndex(JArray columns)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i] as JObject;
                var name = column != null ? GetString(column, "name") : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                // First occurrence wins; a duplicated column name is not something we can meaningfully
                // disambiguate, and overwriting would make the choice depend on column order.
                if (!index.ContainsKey(name))
                {
                    index.Add(name, i);
                }
            }
            return index;
        }

        private static JToken ReadCell(JArray cells, IReadOnlyDictionary<string, int> index, string[] aliases)
        {
            foreach (var alias in aliases)
            {
                if (!index.TryGetValue(alias, out var position)) continue;
                if (position < 0 || position >= cells.Count) continue;

                var cell = cells[position];
                if (cell == null || cell.Type == JTokenType.Null) continue;

                return cell;
            }
            return null;
        }

        private static string ReadString(JArray cells, IReadOnlyDictionary<string, int> index, string[] aliases)
        {
            var cell = ReadCell(cells, index, aliases);
            if (cell == null) return null;

            var value = cell.Type == JTokenType.String ? cell.Value<string>() : cell.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static decimal? ReadDecimal(JArray cells, IReadOnlyDictionary<string, int> index, string[] aliases)
        {
            var cell = ReadCell(cells, index, aliases);
            if (cell == null) return null;

            if (cell.Type == JTokenType.Integer || cell.Type == JTokenType.Float)
            {
                return cell.Value<decimal>();
            }

            return decimal.TryParse(cell.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (decimal?)null;
        }

        /// <summary>
        /// Reads the usage date. With daily granularity Cost Management returns it as the <b>number</b>
        /// 20260901 rather than a date, so the numeric yyyyMMdd form is handled first; an ISO date string is
        /// accepted too, because the shape differs by agreement type.
        /// </summary>
        private static DateTime? ReadDate(JArray cells, IReadOnlyDictionary<string, int> index, string[] aliases)
        {
            var cell = ReadCell(cells, index, aliases);
            if (cell == null) return null;

            if (cell.Type == JTokenType.Integer)
            {
                return FromYyyyMmDd(cell.Value<long>());
            }

            var text = cell.Type == JTokenType.String ? cell.Value<string>() : cell.ToString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                var fromNumeric = FromYyyyMmDd(numeric);
                if (fromNumeric.HasValue) return fromNumeric;
            }

            // AssumeUniversal so a timestamp without an offset is not shifted by the host's time zone, which
            // would move spend onto the wrong day for anyone west of UTC.
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            }

            return null;
        }

        private static DateTime? FromYyyyMmDd(long value)
        {
            if (value < 10000101 || value > 99991231) return null;

            var year = (int)(value / 10000);
            var month = (int)(value / 100 % 100);
            var day = (int)(value % 100);

            if (month < 1 || month > 12) return null;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;

            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }

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

            var value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}

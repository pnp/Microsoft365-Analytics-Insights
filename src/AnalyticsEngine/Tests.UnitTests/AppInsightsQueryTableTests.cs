using DataUtils.AppInsights;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;

namespace Tests.UnitTests
{
    /// <summary>
    /// Parsing tests for the App Insights query-result model used by the in-app Health dashboard.
    /// Pure JSON parsing - no network / App Insights calls.
    /// </summary>
    [TestClass]
    public class AppInsightsQueryTableTests
    {
        // Realistic shape of the App Insights REST query API response (tables/columns/rows).
        private const string SampleJson = @"{
            ""tables"": [
                {
                    ""name"": ""PrimaryResult"",
                    ""columns"": [
                        { ""name"": ""JobName"", ""type"": ""string"" },
                        { ""name"": ""LastCycle"", ""type"": ""datetime"" },
                        { ""name"": ""Count"", ""type"": ""long"" }
                    ],
                    ""rows"": [
                        [ ""Office365ActivityImporter"", ""2026-07-03T10:15:00Z"", 42 ],
                        [ ""AppInsightsImporter"", null, 0 ]
                    ]
                }
            ]
        }";

        [TestMethod]
        public void PrimaryTable_ParsesColumnsAndRows()
        {
            var response = JsonConvert.DeserializeObject<AppInsightsQueryResponse>(SampleJson);
            var table = response.PrimaryTable;

            Assert.AreEqual("PrimaryResult", table.Name);
            Assert.AreEqual(3, table.Columns.Count);
            Assert.AreEqual(2, table.RowCount);
            Assert.AreEqual(2, table.ColumnIndex("Count"));
            Assert.AreEqual(-1, table.ColumnIndex("DoesNotExist"));
        }

        [TestMethod]
        public void TypedAccessors_ReadStringLongAndDate()
        {
            var response = JsonConvert.DeserializeObject<AppInsightsQueryResponse>(SampleJson);
            var table = response.PrimaryTable;
            var row0 = table.Rows[0];

            Assert.AreEqual("Office365ActivityImporter", table.GetString(row0, "JobName"));
            Assert.AreEqual(42L, table.GetLong(row0, "Count"));
            Assert.AreEqual(42, table.GetInt(row0, "Count"));

            var lastCycle = table.GetDateTimeUtc(row0, "LastCycle");
            Assert.IsTrue(lastCycle.HasValue);
            var utc = lastCycle.Value.ToUniversalTime();
            Assert.AreEqual(2026, utc.Year);
            Assert.AreEqual(7, utc.Month);
            Assert.AreEqual(3, utc.Day);
            Assert.AreEqual(10, utc.Hour);
            Assert.AreEqual(15, utc.Minute);
        }

        [TestMethod]
        public void TypedAccessors_HandleNullsAndMissingColumns()
        {
            var response = JsonConvert.DeserializeObject<AppInsightsQueryResponse>(SampleJson);
            var table = response.PrimaryTable;
            var row1 = table.Rows[1];

            // Null cell in the row.
            Assert.IsNull(table.GetDateTimeUtc(row1, "LastCycle"));
            // Present but zero.
            Assert.AreEqual(0L, table.GetLong(row1, "Count"));
            // Column not in the result set.
            Assert.IsNull(table.GetString(row1, "NoSuchColumn"));
            Assert.IsNull(table.GetLong(row1, "NoSuchColumn"));
            Assert.IsNull(table.GetDateTimeUtc(row1, "NoSuchColumn"));
        }

        [TestMethod]
        public void PrimaryTable_PrefersNamedPrimaryResult()
        {
            const string twoTables = @"{
                ""tables"": [
                    { ""name"": ""SomeOtherTable"", ""columns"": [ { ""name"": ""x"", ""type"": ""long"" } ], ""rows"": [ [ 1 ] ] },
                    { ""name"": ""PrimaryResult"", ""columns"": [ { ""name"": ""y"", ""type"": ""long"" } ], ""rows"": [ [ 2 ], [ 3 ] ] }
                ]
            }";

            var response = JsonConvert.DeserializeObject<AppInsightsQueryResponse>(twoTables);
            var table = response.PrimaryTable;

            Assert.AreEqual("PrimaryResult", table.Name);
            Assert.AreEqual(2, table.RowCount);
            Assert.AreEqual(2L, table.GetLong(table.Rows[0], "y"));
        }

        [TestMethod]
        public void PrimaryTable_EmptyWhenNoTables()
        {
            var response = JsonConvert.DeserializeObject<AppInsightsQueryResponse>(@"{ ""tables"": [] }");
            var table = response.PrimaryTable;

            Assert.IsNotNull(table);
            Assert.AreEqual(0, table.RowCount);
        }

        [TestMethod]
        public void ParseConnectionStringValue_ReadsApplicationId()
        {
            const string cs = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://x.applicationinsights.azure.com/;ApplicationId=abc-123";
            Assert.AreEqual("abc-123", AppInsightsQueryClient.ParseConnectionStringValue(cs, "ApplicationId"));
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", AppInsightsQueryClient.ParseConnectionStringValue(cs, "InstrumentationKey"));
            Assert.IsNull(AppInsightsQueryClient.ParseConnectionStringValue(cs, "Missing"));
            Assert.IsNull(AppInsightsQueryClient.ParseConnectionStringValue(null, "ApplicationId"));
        }
    }
}

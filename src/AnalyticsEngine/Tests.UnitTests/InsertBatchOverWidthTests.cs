using Common.Entities;
using DataUtils.Sql;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Verifies that <see cref="InsertBatch{T}"/> skips an individual row whose value is wider than
    /// its staging column (SQL Server error 8152/2628) and still saves the rest of the batch, rather
    /// than throwing and discarding the whole batch (the pre-#127 "poison batch" behaviour). This is
    /// the resilience behind issue #122 / #127, where over-width SharePoint URLs broke imports into
    /// the nvarchar(850) urls.full_url-sized staging columns.
    /// </summary>
    [TestClass]
    public class InsertBatchOverWidthTests
    {
        [TempTableName("##overwidth_test_staging")]
        private class OverWidthTestEntity
        {
            [Column("row_id")]
            public Guid RowId { get; set; }

            // nvarchar(10): anything longer than 10 chars must be skipped, not inserted.
            [Column("v", SqlTypeOverride = "nvarchar(10)")]
            public string Value { get; set; }
        }

        [TestMethod]
        public async Task OverWidthRowIsSkippedAndRestOfBatchIsSaved()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var connectionString = db.Database.Connection.ConnectionString;
                var logger = new CapturingLogger();

                using (var keepAlive = new SqlConnection(connectionString))
                {
                    await keepAlive.OpenAsync();

                    // A global (##) temp results table is visible to InsertBatch's own connections,
                    // and is kept alive by this connection so we can assert on it after
                    // SaveToStagingTable returns (which disposes its own connections + staging table).
                    using (var create = keepAlive.CreateCommand())
                    {
                        create.CommandText =
                            "IF OBJECT_ID('tempdb..##overwidth_test_results') IS NOT NULL DROP TABLE ##overwidth_test_results; " +
                            "CREATE TABLE ##overwidth_test_results (row_id uniqueidentifier, v nvarchar(850));";
                        await create.ExecuteNonQueryAsync();
                    }

                    var okId1 = Guid.NewGuid();
                    var okId2 = Guid.NewGuid();
                    var tooWideId = Guid.NewGuid();

                    var batch = new InsertBatch<OverWidthTestEntity>(connectionString, logger);
                    batch.Rows.Add(new OverWidthTestEntity { RowId = okId1, Value = "short" });
                    batch.Rows.Add(new OverWidthTestEntity { RowId = tooWideId, Value = new string('x', 50) });
                    batch.Rows.Add(new OverWidthTestEntity { RowId = okId2, Value = "alsoshort" });

                    const string mergeSql =
                        "INSERT INTO ##overwidth_test_results (row_id, v) SELECT row_id, v FROM ##overwidth_test_staging;";

                    // Must NOT throw, even though one row is over-width.
                    var survivors = await batch.SaveToStagingTable(mergeSql);

                    Assert.AreEqual(2, survivors, "Merge should have copied exactly the 2 in-width rows.");

                    var savedIds = new List<Guid>();
                    using (var read = keepAlive.CreateCommand())
                    {
                        read.CommandText = "SELECT row_id FROM ##overwidth_test_results;";
                        using (var reader = await read.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync()) savedIds.Add(reader.GetGuid(0));
                        }
                    }

                    CollectionAssert.Contains(savedIds, okId1, "First in-width row should be saved.");
                    CollectionAssert.Contains(savedIds, okId2, "Second in-width row should be saved.");
                    CollectionAssert.DoesNotContain(savedIds, tooWideId, "Over-width row must be skipped, not saved.");

                    Assert.IsTrue(
                        logger.Messages.Any(m => m.Contains("Skipping over-width record") && m.Contains("'v'")),
                        "The skip should be logged identifying the offending column. Captured: " + string.Join(" | ", logger.Messages));

                    using (var drop = keepAlive.CreateCommand())
                    {
                        drop.CommandText = "IF OBJECT_ID('tempdb..##overwidth_test_results') IS NOT NULL DROP TABLE ##overwidth_test_results;";
                        await drop.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        /// <summary>Minimal <see cref="ILogger"/> that records formatted messages for assertions.</summary>
        private class CapturingLogger : ILogger
        {
            public readonly List<string> Messages = new List<string>();
            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Messages.Add(formatter(state, exception));
            }

            private class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }
    }
}

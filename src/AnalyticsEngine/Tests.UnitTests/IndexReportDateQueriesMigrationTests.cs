using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.SqlClient;

namespace Tests.UnitTests
{
    [TestClass]
    public class IndexReportDateQueriesMigrationTests
    {
        [TestMethod]
        public void UpSql_CreatesExpectedCoveringIndexes_AndIsIdempotent()
        {
            using (var db = new AnalyticsEntitiesContext())
            using (var connection = new SqlConnection(db.Database.Connection.ConnectionString))
            {
                connection.Open();
                Execute(connection, IndexReportDateQueries.Up_Sql);
                Execute(connection, IndexReportDateQueries.Up_Sql);

                AssertIndexColumn(connection, "audit_events", "IX_audit_events_time_stamp", "time_stamp", 1, false);
                AssertIndexColumn(connection, "audit_events", "IX_audit_events_time_stamp", "operation_id", 0, true);
                AssertIndexColumn(connection, "audit_events", "IX_audit_events_time_stamp", "user_id", 0, true);
                AssertIndexColumn(connection, "hits", "IX_hits_hit_timestamp", "hit_timestamp", 1, false);
                AssertIndexColumn(connection, "hits", "IX_hits_hit_timestamp", "session_id", 0, true);
                AssertIndexColumn(connection, "call_records", "IX_call_records_start", "start", 1, false);
                AssertIndexColumn(connection, "call_records", "IX_call_records_start", "end", 0, true);
                AssertIndexColumn(connection, "sent_emails", "IX_sent_emails_sent_date", "sent_date", 1, false);
            }
        }

        private static void AssertIndexColumn(
            SqlConnection connection,
            string table,
            string index,
            string column,
            int keyOrdinal,
            bool included)
        {
            using (var command = new SqlCommand(
                @"SELECT COUNT(*)
                  FROM sys.indexes AS i
                  JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                  JOIN sys.columns AS c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE i.object_id = OBJECT_ID(@table)
                    AND i.name = @index
                    AND c.name = @column
                    AND ic.key_ordinal = @keyOrdinal
                    AND ic.is_included_column = @included;", connection))
            {
                command.Parameters.AddWithValue("@table", "dbo." + table);
                command.Parameters.AddWithValue("@index", index);
                command.Parameters.AddWithValue("@column", column);
                command.Parameters.AddWithValue("@keyOrdinal", keyOrdinal);
                command.Parameters.AddWithValue("@included", included);
                Assert.AreEqual(1, (int)command.ExecuteScalar(),
                    $"Unexpected definition for {index}.{column}.");
            }
        }

        private static void Execute(SqlConnection connection, string sql)
        {
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
            {
                command.ExecuteNonQuery();
            }
        }
    }
}

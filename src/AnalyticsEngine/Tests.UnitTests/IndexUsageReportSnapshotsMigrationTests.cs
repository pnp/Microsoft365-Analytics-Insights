using Common.Entities;
using Common.Entities.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.SqlClient;

namespace Tests.UnitTests
{
    [TestClass]
    public class IndexUsageReportSnapshotsMigrationTests
    {
        private static readonly string[] Tables =
        {
            "teams_user_activity_log",
            "outlook_user_activity_log",
            "onedrive_user_activity_log",
            "sharepoint_user_activity_log",
            "yammer_user_activity_log",
        };

        [TestMethod]
        public void UpSql_CreatesExpectedCoveringIndexes_AndIsIdempotent()
        {
            using (var db = new AnalyticsEntitiesContext())
            using (var connection = new SqlConnection(db.Database.Connection.ConnectionString))
            {
                connection.Open();
                Execute(connection, IndexUsageReportSnapshots.Up_Sql);
                Execute(connection, IndexUsageReportSnapshots.Up_Sql);

                foreach (var table in Tables)
                {
                    var index = $"IX_{table}_report_snapshot";
                    Assert.AreEqual(1, Scalar(
                        connection,
                        @"SELECT COUNT(*)
                          FROM sys.indexes
                          WHERE object_id = OBJECT_ID(@table) AND name = @index;",
                        table,
                        index), $"Expected {index} to exist.");

                    AssertIndexColumn(connection, table, index, "date", 1, false);
                    AssertIndexColumn(connection, table, index, "last_activity_date", 2, false);
                    AssertIndexColumn(connection, table, index, "user_id", 0, true);
                }
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

        private static int Scalar(
            SqlConnection connection,
            string sql,
            string table,
            string index)
        {
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@table", "dbo." + table);
                command.Parameters.AddWithValue("@index", index);
                return (int)command.ExecuteScalar();
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

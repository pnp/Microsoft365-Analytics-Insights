using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// A throwaway SQL Server database for exercising raw-SQL schema migrations.
    ///
    /// Index migrations have to be tested against real <c>sys.indexes</c> metadata, but running them
    /// against the shared unit-test database would leave its indexes permanently redefined for every
    /// other test (and would make the "index already exists in its old shape" rebuild path
    /// untestable, because the first run would have already fixed it). Each test therefore builds
    /// its own database on the same server, creates just the tables the migration touches, and drops
    /// the database again on dispose.
    /// </summary>
    internal sealed class ScratchDatabase : IDisposable
    {
        private readonly string _databaseName;
        private readonly string _masterConnectionString;
        private bool _dropped;

        public string ConnectionString { get; }

        private ScratchDatabase(string databaseName, string masterConnectionString, string connectionString)
        {
            _databaseName = databaseName;
            _masterConnectionString = masterConnectionString;
            ConnectionString = connectionString;
        }

        /// <summary>Creates an empty database on the same server as the unit-test database.</summary>
        public static ScratchDatabase Create(string purpose)
        {
            // Read the connection string straight from configuration rather than constructing an
            // AnalyticsEntitiesContext: in a DEBUG build that context's initializer migrates the
            // shared unit-test database to the latest schema as a side effect, and these tests only
            // need the server details.
            var configured = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"];
            if (configured == null)
            {
                throw new InvalidOperationException(
                    "The 'SPOInsightsEntities' connection string is missing, so no server is available to create a scratch database on.");
            }

            // Unique per test run so parallel or repeated runs never collide. Well within SQL
            // Server's 128-character identifier limit, so no truncation is needed.
            var name = $"UT_{purpose}_{Guid.NewGuid():N}";

            var master = new SqlConnectionStringBuilder(configured.ConnectionString) { InitialCatalog = "master" };
            var scratch = new SqlConnectionStringBuilder(configured.ConnectionString) { InitialCatalog = name };

            var database = new ScratchDatabase(name, master.ConnectionString, scratch.ConnectionString);
            ExecuteOn(master.ConnectionString, $"CREATE DATABASE [{name}];");
            return database;
        }

        public void Execute(string sql) => ExecuteOn(ConnectionString, sql);

        /// <summary>
        /// Runs a GO-separated script the way a DBA's client would: one connection for the whole script,
        /// batch by batch, with <c>QUOTED_IDENTIFIER</c> pinned to <paramref name="quotedIdentifierOn"/>
        /// for the session.
        /// </summary>
        /// <remarks>
        /// The manual upgrade scripts are the path CI otherwise never executes - the tests run the
        /// migration's <c>Up_Sql</c> through SqlClient instead, which is a different client with different
        /// defaults. sqlcmd defaults <c>QUOTED_IDENTIFIER</c> to OFF while SSMS and SqlClient default it
        /// ON, and <c>CREATE</c>/<c>ALTER VIEW</c> permanently captures whichever was in force. Passing
        /// <c>false</c> therefore reproduces the by-hand upgrade rather than approximating it.
        ///
        /// Unlike sqlcmd this does NOT continue past a failed batch: a severity-16 <c>RAISERROR</c>
        /// surfaces as a <see cref="SqlException"/> so a test cannot pass while the script is reporting
        /// that it refused to stamp.
        /// </remarks>
        public void ExecuteScript(string sql, bool quotedIdentifierOn)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                connection.Open();

                using (var session = new SqlCommand(
                    quotedIdentifierOn ? "SET QUOTED_IDENTIFIER ON;" : "SET QUOTED_IDENTIFIER OFF;", connection))
                {
                    session.ExecuteNonQuery();
                }

                foreach (var batch in SplitOnGo(sql))
                {
                    using (var command = new SqlCommand(batch, connection) { CommandTimeout = 0 })
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Splits on the <c>GO</c> batch separator. <c>GO</c> is a client-side convention rather than
        /// T-SQL, so it has to be handled here exactly as sqlcmd does: alone on its line, any case.
        /// </summary>
        /// <remarks>
        /// The <c>\r?</c> is load-bearing. These scripts are CRLF, and in .NET multiline mode <c>$</c>
        /// matches immediately before the <c>\n</c> - so without it the trailing <c>\r</c> sits between
        /// <c>GO</c> and the anchor, nothing matches, and the whole script is sent as one batch that
        /// fails with "Incorrect syntax near 'GO'".
        /// </remarks>
        private static IEnumerable<string> SplitOnGo(string sql)
        {
            foreach (var batch in Regex.Split(sql, @"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    yield return batch;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="index"/> exists on <paramref name="table"/> with
        /// <paramref name="column"/> at the given key position (0 = an INCLUDE column).
        /// </summary>
        public bool IndexHasColumn(string table, string index, string column, int keyOrdinal, bool included)
        {
            const string sql =
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
                    AND ic.is_included_column = @included;";

            using (var connection = new SqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@table", "dbo." + table);
                    command.Parameters.AddWithValue("@index", index);
                    command.Parameters.AddWithValue("@column", column);
                    command.Parameters.AddWithValue("@keyOrdinal", keyOrdinal);
                    command.Parameters.AddWithValue("@included", included);
                    return (int)command.ExecuteScalar() == 1;
                }
            }
        }

        /// <summary>How many columns <paramref name="index"/> has (keys + includes), 0 when absent.</summary>
        public int IndexColumnCount(string table, string index)
        {
            const string sql =
                @"SELECT COUNT(*)
                  FROM sys.indexes AS i
                  JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                  WHERE i.object_id = OBJECT_ID(@table) AND i.name = @index;";

            using (var connection = new SqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@table", "dbo." + table);
                    command.Parameters.AddWithValue("@index", index);
                    return (int)command.ExecuteScalar();
                }
            }
        }

        /// <summary>How many non-clustered indexes exist on the table (to catch duplicates).</summary>
        public int NonClusteredIndexCount(string table)
        {
            const string sql =
                @"SELECT COUNT(*)
                  FROM sys.indexes
                  WHERE object_id = OBJECT_ID(@table) AND type_desc = N'NONCLUSTERED';";

            using (var connection = new SqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@table", "dbo." + table);
                    return (int)command.ExecuteScalar();
                }
            }
        }

        public bool IndexExists(string table, string index) => IndexColumnCount(table, index) > 0;

        /// <summary>Runs a scalar query. Returns <c>null</c> for <c>DBNULL</c>.</summary>
        public object Scalar(string sql)
        {
            using (var connection = new SqlConnection(ConnectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
                {
                    var value = command.ExecuteScalar();
                    return value == DBNull.Value ? null : value;
                }
            }
        }

        private static void ExecuteOn(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
                {
                    command.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            if (_dropped)
            {
                return;
            }
            _dropped = true;

            try
            {
                ExecuteOn(
                    _masterConnectionString,
                    $@"IF DB_ID(N'{_databaseName}') IS NOT NULL
                       BEGIN
                           ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                           DROP DATABASE [{_databaseName}];
                       END");
            }
            catch (SqlException)
            {
                // A scratch database left behind by an aborted run is noise, not a test failure.
            }
        }
    }
}

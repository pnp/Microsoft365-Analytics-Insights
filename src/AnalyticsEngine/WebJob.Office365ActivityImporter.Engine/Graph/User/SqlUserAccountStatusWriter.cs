using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Write port for marking <c>dbo.users</c> rows disabled when Graph reports their account disabled or deleted.
    /// Internal, like <see cref="IUserBulkUpdateWriter"/>; a test can substitute one that fails.
    /// </summary>
    internal interface IUserAccountStatusWriter
    {
        /// <summary>
        /// Sets <c>account_enabled = 0</c> and <c>last_updated</c> on each row that still carries the object id it was
        /// selected by and is not disabled already. Writes nothing else. Returns how many rows changed.
        /// </summary>
        Task<int> MarkDisabledAsync(IReadOnlyList<UserRowDeactivation> rows, DateTime lastUpdatedUtc);
    }

    /// <summary>SQL Server implementation of <see cref="IUserAccountStatusWriter"/>.</summary>
    /// <remarks>
    /// Joins on the primary key, so each row is a clustered-index seek - <c>azure_ad_id</c> is unindexable
    /// <c>nvarchar(max)</c>, and is only compared on the row the seek found. Only <c>account_enabled</c> and
    /// <c>last_updated</c> are written: a deleted account's entry carries nothing but its id, and running it through
    /// the normal mapping would clear its department, manager and the rest.
    /// </remarks>
    internal sealed class SqlUserAccountStatusWriter : IUserAccountStatusWriter
    {
        /// <summary>Two parameters a row, so well under SQL Server's 2,100-parameter limit.</summary>
        internal const int RowsPerStatement = 500;

        private readonly string _connectionString;

        public SqlUserAccountStatusWriter(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }
            _connectionString = connectionString;
        }

        public async Task<int> MarkDisabledAsync(IReadOnlyList<UserRowDeactivation> rows, DateTime lastUpdatedUtc)
        {
            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            var changed = 0;
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync();
                for (var offset = 0; offset < rows.Count; offset += RowsPerStatement)
                {
                    var count = Math.Min(RowsPerStatement, rows.Count - offset);
                    using (var command = new SqlCommand { Connection = connection, CommandTimeout = 600 })
                    {
                        var sql = new StringBuilder("UPDATE u SET account_enabled = 0, last_updated = @lastUpdated FROM dbo.users AS u INNER JOIN (VALUES ");
                        for (var i = 0; i < count; i++)
                        {
                            if (i > 0)
                            {
                                sql.Append(',');
                            }
                            sql.Append("(@i").Append(i).Append(",@a").Append(i).Append(')');
                            command.Parameters.Add("@i" + i, SqlDbType.Int).Value = rows[offset + i].UserId;
                            command.Parameters.Add("@a" + i, SqlDbType.NVarChar, 450).Value = rows[offset + i].ObjectId;
                        }
                        sql.Append(") AS v(id, azure_ad_id) ON u.id = v.id")
                           .Append(" WHERE u.azure_ad_id = v.azure_ad_id AND (u.account_enabled IS NULL OR u.account_enabled <> 0);");

                        command.Parameters.Add("@lastUpdated", SqlDbType.DateTime).Value = lastUpdatedUtc;
                        command.CommandText = sql.ToString();
                        changed += await command.ExecuteNonQueryAsync();
                    }
                }
            }
            return changed;
        }
    }
}

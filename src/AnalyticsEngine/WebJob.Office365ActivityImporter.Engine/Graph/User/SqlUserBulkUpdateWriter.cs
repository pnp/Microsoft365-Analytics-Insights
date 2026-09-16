using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using DataUtils.Sql;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// SQL Server implementation of <see cref="IUserBulkUpdateWriter"/>: creates a session temp
    /// table, <c>SqlBulkCopy</c>s the batch into it, runs a single <c>UPDATE ... FROM JOIN</c> and
    /// drops the temp table again.
    /// </summary>
    /// <remarks>
    /// This is <c>UserBatchProcessor.ExecuteBulkUpdate</c> moved here unchanged (#371): same
    /// connection handling, same temp-table DDL, same column mappings, same batch size and
    /// timeouts. Nothing about the bulk-copy strategy is being altered - it is only being taken out
    /// of the batching logic so that logic becomes testable.
    ///
    /// The temp table is <c>#user_updates</c>, i.e. session-scoped, so every step must run on the
    /// same open <see cref="SqlConnection"/>.
    /// </remarks>
    internal class SqlUserBulkUpdateWriter : IUserBulkUpdateWriter
    {
        private readonly string _connectionString;

        public SqlUserBulkUpdateWriter(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }
            _connectionString = connectionString;
        }

        public async Task ExecuteAsync(DataTable userUpdates)
        {
            if (userUpdates.Rows.Count == 0)
                return;

            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(CREATE_TEMP_TABLE_SQL, connection))
                {
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var bulkCopy = new SqlBulkCopy(connection))
                {
                    bulkCopy.DestinationTableName = "#user_updates";
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = 600;

                    foreach (var column in UserBulkUpdateRules.UpdateTableColumns)
                    {
                        bulkCopy.ColumnMappings.Add(column, column);
                    }

                    await bulkCopy.WriteToServerAsync(userUpdates);
                }

                using (var cmd = new SqlCommand(UPDATE_FROM_TEMP_SQL, connection))
                {
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = new SqlCommand("DROP TABLE #user_updates", connection))
                {
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        internal const string CREATE_TEMP_TABLE_SQL = @"
            CREATE TABLE #user_updates (
                id              INT          NOT NULL,
                azure_ad_id     NVARCHAR(450) NULL,
                account_enabled BIT           NULL,
                created_utc     DATETIME2(7)  NULL,
                mail            NVARCHAR(450) NULL,
                postalcode      NVARCHAR(50)  NULL,
                department_id   INT           NULL,
                job_title_id    INT           NULL,
                office_location_id INT        NULL,
                usage_location_id  INT        NULL,
                country_or_region_id INT      NULL,
                state_or_province_id INT      NULL,
                company_name_id INT           NULL,
                manager_id      INT           NULL,
                last_updated    DATETIME      NOT NULL
            )";

        /// <summary>
        /// Applies one batch to <c>dbo.users</c>.
        /// </summary>
        /// <remarks>
        /// <c>created_utc</c> is written with a coalescing guard rather than straight through. It
        /// carries Entra's immutable <c>user.createdDateTime</c>, so a NULL arriving from Graph only
        /// ever means "this response did not include it" - never "this user has no creation date".
        /// The <c>/users/delta</c> <c>$select</c> is fixed when the delta token is first minted, so a
        /// tenant that upgrades with a stored token can keep receiving responses without it. Writing
        /// those NULLs through would blank the column for every user Graph reports as changed, which
        /// would silently demote every never-used Copilot seat from "probable" to "review" and empty
        /// the reclaim recommendation. Every other column here is genuinely mutable and is written as
        /// supplied.
        /// </remarks>
        internal const string UPDATE_FROM_TEMP_SQL = @"
            UPDATE u
            SET u.azure_ad_id            = t.azure_ad_id,
                u.account_enabled        = t.account_enabled,
                u.created_utc            = ISNULL(t.created_utc, u.created_utc),
                u.mail                   = t.mail,
                u.postalcode             = t.postalcode,
                u.department_id          = t.department_id,
                u.job_title_id           = t.job_title_id,
                u.office_location_id     = t.office_location_id,
                u.usage_location_id      = t.usage_location_id,
                u.country_or_region_id   = t.country_or_region_id,
                u.state_or_province_id   = t.state_or_province_id,
                u.company_name_id        = t.company_name_id,
                u.manager_id             = t.manager_id,
                u.last_updated           = t.last_updated
            FROM dbo.users u
            INNER JOIN #user_updates t ON u.id = t.id";
    }
}

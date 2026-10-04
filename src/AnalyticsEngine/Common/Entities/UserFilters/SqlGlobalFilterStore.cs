using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserFilters
{
    /// <summary>The administrator's global filter as stored, with when and by whom it was last changed.</summary>
    public sealed class GlobalFilterRecord
    {
        /// <summary>
        /// False when the database predates <c>dbo.portal_global_filters</c>: no global filter is in force,
        /// and none can be saved until the database is upgraded.
        /// </summary>
        public bool StorageAvailable { get; set; }

        /// <summary>The definition's wire form (<see cref="GlobalFilterCodec"/>). Empty for no filter.</summary>
        public string FilterJson { get; set; } = string.Empty;

        /// <summary>Moves on every save. Zero when no filter has ever been saved.</summary>
        public int Revision { get; set; }

        public DateTime? ModifiedUtc { get; set; }

        /// <summary>The sign-in name of the administrator who last saved it.</summary>
        public string ModifiedBy { get; set; }
    }

    /// <summary>Reads and writes the administrator's global filter.</summary>
    /// <remarks>A port, so the web app's provider and API can be tested without a database.</remarks>
    public interface IGlobalFilterStore
    {
        Task<GlobalFilterRecord> GetAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Saves the filter when the stored revision is still <paramref name="expectedRevision"/>, and
        /// returns what was saved. <c>null</c> when somebody else saved first.
        /// </summary>
        /// <exception cref="GlobalFilterStorageMissingException">The database has not been upgraded to hold a global filter.</exception>
        Task<GlobalFilterRecord> SaveAsync(string filterJson, int expectedRevision, string modifiedBy, CancellationToken cancellationToken);
    }

    /// <summary>The database predates the global filter table, so a filter cannot be saved.</summary>
    public sealed class GlobalFilterStorageMissingException : InvalidOperationException
    {
        public GlobalFilterStorageMissingException()
            : base("The database has not been upgraded to hold a global filter. Run the installer, or the manual upgrade script for 202610011330001_PortalGlobalFilter.")
        {
        }
    }

    /// <summary>Builds the SQL Server adapter for <see cref="IGlobalFilterStore"/>.</summary>
    public static class GlobalFilterStores
    {
        public static IGlobalFilterStore Create(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A connection string to the Analytics database is required.", nameof(connectionString));
            }

            return new SqlGlobalFilterStore(connectionString);
        }
    }

    /// <summary>
    /// SQL Server implementation of <see cref="IGlobalFilterStore"/>, over <c>dbo.portal_global_filters</c>.
    /// </summary>
    /// <remarks>
    /// Raw SQL rather than EF, like the user organisation stores: the table is created by a migration that
    /// changes no entity model. A database that has not been upgraded yet reads as "no filter", so a portal
    /// deployed ahead of its database keeps working.
    /// </remarks>
    internal sealed class SqlGlobalFilterStore : IGlobalFilterStore
    {
        private const int CommandTimeoutSeconds = 30;
        internal const int MaxModifiedByLength = 256;

        internal const string ReadSql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.portal_global_filters', N'U') IS NULL
    SELECT CAST(0 AS bit) AS available, CAST(N'' AS nvarchar(max)) AS filter_json, 0 AS revision,
           CAST(NULL AS datetime2(7)) AS modified_utc, CAST(NULL AS nvarchar(256)) AS modified_by;
ELSE
    SELECT CAST(1 AS bit) AS available, ISNULL(f.filter_json, N'') AS filter_json, ISNULL(f.revision, 0) AS revision,
           f.modified_utc, f.modified_by
    FROM (SELECT 1 AS one) AS anchor
    LEFT JOIN dbo.portal_global_filters AS f ON f.id = 1;";

        // Serialised on the row (or, before the first save, on the key range) so two administrators saving
        // from the same revision cannot both succeed: the second sees the first's revision and is refused.
        internal const string SaveSql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.portal_global_filters', N'U') IS NULL
BEGIN
    SELECT CAST(0 AS bit) AS available, CAST(0 AS bit) AS saved, 0 AS revision,
           CAST(NULL AS datetime2(7)) AS modified_utc, CAST(NULL AS nvarchar(256)) AS modified_by;
    RETURN;
END

BEGIN TRANSACTION;

DECLARE @current int = (SELECT revision FROM dbo.portal_global_filters WITH (UPDLOCK, HOLDLOCK) WHERE id = 1);

IF ISNULL(@current, 0) <> @expectedRevision
BEGIN
    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit) AS available, CAST(0 AS bit) AS saved, ISNULL(@current, 0) AS revision,
           CAST(NULL AS datetime2(7)) AS modified_utc, CAST(NULL AS nvarchar(256)) AS modified_by;
    RETURN;
END

-- What this save wrote, captured inside the transaction: a SELECT after the COMMIT could read a later save's
-- revision, and an editor handed that revision could then overwrite the later save without a conflict.
DECLARE @saved TABLE (revision int NOT NULL, modified_utc datetime2(7) NULL, modified_by nvarchar(256) NULL);

IF @current IS NULL
    INSERT INTO dbo.portal_global_filters (id, filter_json, revision, modified_utc, modified_by)
    OUTPUT inserted.revision, inserted.modified_utc, inserted.modified_by INTO @saved
    VALUES (1, @filterJson, 1, SYSUTCDATETIME(), @modifiedBy);
ELSE
    UPDATE dbo.portal_global_filters
    SET filter_json = @filterJson, revision = revision + 1, modified_utc = SYSUTCDATETIME(), modified_by = @modifiedBy
    OUTPUT inserted.revision, inserted.modified_utc, inserted.modified_by INTO @saved
    WHERE id = 1;

COMMIT TRANSACTION;

SELECT CAST(1 AS bit) AS available, CAST(1 AS bit) AS saved, revision, modified_utc, modified_by
FROM @saved;";

        private readonly string _connectionString;

        public SqlGlobalFilterStore(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task<GlobalFilterRecord> GetAsync(CancellationToken cancellationToken)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var cmd = new SqlCommand(ReadSql, connection) { CommandTimeout = CommandTimeoutSeconds })
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return new GlobalFilterRecord { StorageAvailable = false };
                    }

                    return new GlobalFilterRecord
                    {
                        StorageAvailable = reader.GetBoolean(0),
                        FilterJson = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        Revision = reader.GetInt32(2),
                        ModifiedUtc = reader.IsDBNull(3) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                        ModifiedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                    };
                }
            }
        }

        public async Task<GlobalFilterRecord> SaveAsync(string filterJson, int expectedRevision, string modifiedBy, CancellationToken cancellationToken)
        {
            var by = string.IsNullOrWhiteSpace(modifiedBy) ? null : modifiedBy.Trim();
            if (by != null && by.Length > MaxModifiedByLength) by = by.Substring(0, MaxModifiedByLength);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await SaveOnceAsync(filterJson, expectedRevision, by, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlException ex) when (IsDeadlock(ex) && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<GlobalFilterRecord> SaveOnceAsync(string filterJson, int expectedRevision, string by, CancellationToken cancellationToken)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var cmd = new SqlCommand(SaveSql, connection) { CommandTimeout = CommandTimeoutSeconds })
                {
                    cmd.Parameters.Add(new SqlParameter("@filterJson", SqlDbType.NVarChar, -1) { Value = filterJson ?? string.Empty });
                    cmd.Parameters.Add(new SqlParameter("@expectedRevision", SqlDbType.Int) { Value = expectedRevision });
                    cmd.Parameters.Add(new SqlParameter("@modifiedBy", SqlDbType.NVarChar, MaxModifiedByLength) { Value = (object)by ?? DBNull.Value });

                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0))
                        {
                            throw new GlobalFilterStorageMissingException();
                        }

                        if (!reader.GetBoolean(1)) return null;

                        return new GlobalFilterRecord
                        {
                            StorageAvailable = true,
                            FilterJson = filterJson ?? string.Empty,
                            Revision = reader.GetInt32(2),
                            ModifiedUtc = reader.IsDBNull(3) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                            ModifiedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                        };
                    }
                }
            }
        }

        private static bool IsDeadlock(SqlException ex)
        {
            foreach (SqlError error in ex.Errors)
            {
                if (error.Number == 1205) return true;
            }

            return false;
        }
    }
}

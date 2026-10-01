using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserScope.Purge
{
    /// <summary>
    /// The analytics database as a purge uses it. A purge keeps no state of its own here - no table, no schema object:
    /// its record lives in <see cref="UserScopePurgeStateStore"/>. Two things touch SQL Server, and neither outlives the
    /// purge:
    /// <list type="bullet">
    /// <item><description>The one-purge-at-a-time rule is an application lock (<c>sp_getapplock</c>) owned by the session
    /// running the purge. SQL Server releases it when that session ends, however it ends, so a web app instance that
    /// crashes never leaves a purge locked, and every instance agrees about it without sharing any other state.</description></item>
    /// <item><description>The users being removed are a temporary table on that session, which every purge statement
    /// joins to. It goes with the session.</description></item>
    /// </list>
    /// </summary>
    public sealed class UserScopePurgeDatabase
    {
        /// <summary>The application lock that only the session running a purge holds.</summary>
        internal const string LockResource = "AnalyticsInsights.UserScopePurge";

        private readonly string _connectionString;

        public UserScopePurgeDatabase(string connectionString)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>A new, open connection for a short read or write, with an Entra token when the server needs one.</summary>
        internal Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
            => OpenAsync(_connectionString, cancellationToken);

        private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
        {
            var connection = AzureSqlTokenAuth.CreateConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens a session of its own and takes the purge lock on it, waiting up to <paramref name="wait"/> for it. Null
        /// when another session holds the lock: a purge is running, on this web app instance or another.
        /// </summary>
        public async Task<UserScopePurgeSession> TryOpenPurgeSessionAsync(TimeSpan wait, CancellationToken cancellationToken = default)
        {
            var connection = await OpenLockedConnectionAsync(wait, cancellationToken).ConfigureAwait(false);
            return connection == null ? null : new UserScopePurgeSession(this, connection);
        }

        /// <summary>An open connection whose session holds the purge lock, or null when another session holds it.</summary>
        internal async Task<SqlConnection> OpenLockedConnectionAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            // Unpooled: closing the connection has to end the session, which is what releases the lock. A pooled
            // connection would go back to the pool still holding it.
            var unpooled = new SqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString;
            var connection = await OpenAsync(unpooled, cancellationToken).ConfigureAwait(false);
            try
            {
                const string sql = @"
DECLARE @result int;
EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeoutMs;
SELECT @result;";
                using (var command = new SqlCommand(sql, connection))
                {
                    command.CommandTimeout = (int)Math.Ceiling(wait.TotalSeconds) + 30;
                    command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                    command.Parameters.Add("@timeoutMs", SqlDbType.Int).Value = (int)Math.Max(0, wait.TotalMilliseconds);

                    // 0: granted straight away; 1: granted after waiting; negative: held elsewhere, or not granted.
                    if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) >= 0)
                    {
                        return connection;
                    }
                }
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            connection.Dispose();
            return null;
        }

        /// <summary>Whether a purge is running anywhere: some session holds the purge lock.</summary>
        public async Task<bool> IsPurgeRunningAsync(CancellationToken cancellationToken = default)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var command = new SqlCommand("SELECT APPLOCK_TEST('public', @resource, 'Exclusive', 'Session');", connection))
            {
                command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = LockResource;
                return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0;
            }
        }

        /// <summary>
        /// The users in the database and how many of them <paramref name="scope"/> leaves out - what a purge would remove.
        /// The anonymous Unknown User is never counted as outside: it stands in for people, it isn't one.
        /// </summary>
        public async Task<(int Total, int OutsideScope)> CountUsersAsync(UserImportScope scope)
        {
            var total = 0;
            var outside = 0;
            using (var connection = await OpenAsync().ConfigureAwait(false))
            using (var command = new SqlCommand("SELECT user_name, mail, azure_ad_id FROM dbo.users;", connection))
            {
                command.CommandTimeout = 600;
                using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var upn = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var mail = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var objectId = reader.IsDBNull(2) ? null : reader.GetString(2);
                        if (string.Equals(upn, UserScopePurgePlan.UnknownUserName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        total++;
                        if (!scope.IsAnyInScope(objectId, upn, mail))
                        {
                            outside++;
                        }
                    }
                }
            }
            return (total, outside);
        }
    }

    /// <summary>
    /// The session running a purge. It holds the purge lock, and its <c>#purge_candidates</c> table lists the users being
    /// removed. If its connection breaks, <see cref="EnsureOpenAsync"/> opens a new one, takes the lock again and reloads
    /// the list, so a transient fault doesn't stop a purge that can take hours.
    /// </summary>
    public sealed class UserScopePurgeSession : IDisposable
    {
        private readonly UserScopePurgeDatabase _database;
        private IReadOnlyCollection<int> _candidates = Array.Empty<int>();

        internal UserScopePurgeSession(UserScopePurgeDatabase database, SqlConnection connection)
        {
            _database = database;
            Connection = connection;
        }

        /// <summary>The session's connection; replaced when <see cref="EnsureOpenAsync"/> has to re-open it.</summary>
        internal SqlConnection Connection { get; private set; }

        /// <summary>How long re-opening waits for SQL Server to let go of a broken session's lock.</summary>
        internal TimeSpan ReopenWait { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Makes <paramref name="userIds"/> the session's <c>#purge_candidates</c>.</summary>
        internal async Task LoadCandidatesAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
        {
            _candidates = userIds ?? Array.Empty<int>();
            await FillCandidatesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task FillCandidatesAsync(CancellationToken cancellationToken)
        {
            const string create = @"
IF OBJECT_ID(N'tempdb..#purge_candidates') IS NOT NULL DROP TABLE #purge_candidates;
CREATE TABLE #purge_candidates (user_id int NOT NULL PRIMARY KEY CLUSTERED);";
            using (var command = new SqlCommand(create, Connection))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_candidates.Count == 0)
            {
                return;
            }

            var table = new DataTable();
            table.Columns.Add("user_id", typeof(int));
            foreach (var id in _candidates.Distinct().OrderBy(id => id))
            {
                table.Rows.Add(id);
            }

            using (var bulk = new SqlBulkCopy(Connection) { DestinationTableName = "#purge_candidates", BatchSize = 10000, BulkCopyTimeout = 600 })
            {
                await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Candidates whose user row is still there: kept because new data about them arrived while the purge ran.</summary>
        internal async Task<int> CountRemainingCandidatesAsync()
        {
            const string sql = "SELECT COUNT(*) FROM #purge_candidates c WHERE EXISTS (SELECT 1 FROM dbo.users u WHERE u.id = c.user_id);";
            using (var command = new SqlCommand(sql, Connection))
            {
                command.CommandTimeout = 600;
                return (int)await command.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Drops a connection that can no longer be trusted, so that <see cref="EnsureOpenAsync"/> opens the session again.
        /// The lock went with the old session (SQL Server releases it when it notices the session has gone).
        /// </summary>
        /// <remarks>
        /// Once discarded, a connection is never used again, whatever its <c>State</c> says. After its session is killed,
        /// SqlClient closes the broken connection on an I/O thread of its own, and while it does the connection still
        /// reports <c>Open</c>: a command sent to it then fails with "requires an open and available Connection. The
        /// connection's current state is open" - an <see cref="InvalidOperationException"/>, which no SQL retry catches.
        /// </remarks>
        internal void DiscardConnection()
        {
            _discarded = true;
            try
            {
                Connection.Dispose();
            }
            catch (Exception)
            {
                // Already being torn down by SqlClient; it is never used again either way.
            }
        }

        private bool _discarded;

        /// <summary>
        /// Re-opens the session when its connection has broken: takes the purge lock again and reloads the candidates.
        /// Throws <see cref="UserScopePurgeLockLostException"/> if another session took the lock meanwhile.
        /// </summary>
        internal async Task EnsureOpenAsync(CancellationToken cancellationToken = default)
        {
            if (!_discarded && Connection.State == ConnectionState.Open)
            {
                return;
            }

            _discarded = true;
            try
            {
                Connection.Dispose();
            }
            catch (Exception)
            {
                // As in DiscardConnection.
            }

            var connection = await _database.OpenLockedConnectionAsync(ReopenWait, cancellationToken).ConfigureAwait(false);
            if (connection == null)
            {
                throw new UserScopePurgeLockLostException();
            }

            Connection = connection;

            // Healthy again only once the candidates are back: if reloading them fails, the next attempt opens the session
            // afresh rather than carrying on with a partial list.
            await FillCandidatesAsync(cancellationToken).ConfigureAwait(false);
            _discarded = false;
        }

        public void Dispose()
        {
            // Closing the unpooled connection ends the session, and SQL Server releases the lock with it. Release it
            // explicitly first as well, so that it is free the moment this returns - best effort, because a connection
            // that has just broken can change state between the check and the call.
            try
            {
                if (!_discarded && Connection.State == ConnectionState.Open)
                {
                    using (var command = new SqlCommand("EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", Connection))
                    {
                        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = UserScopePurgeDatabase.LockResource;
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception)
            {
                // The session is going anyway; closing it releases the lock.
            }

            try
            {
                Connection.Dispose();
            }
            catch (Exception)
            {
                // As above.
            }
        }
    }

    /// <summary>
    /// The session running a purge broke, and another session took the purge lock before this one could open a new one.
    /// That session now runs the purge.
    /// </summary>
    public sealed class UserScopePurgeLockLostException : Exception
    {
        public UserScopePurgeLockLostException()
            : base("Another session took the user scope purge lock while this one was reconnecting.")
        {
        }
    }
}

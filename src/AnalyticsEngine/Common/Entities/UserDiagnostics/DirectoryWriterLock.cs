using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserDiagnostics
{
    /// <summary>
    /// The SQL Server application lock that orders every writer of users and licences: the scheduled user import and a
    /// targeted one-person refresh. Exclusive and session-owned, so it lives exactly as long as <see cref="Connection"/>
    /// and is released if the process dies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a database lock and not the queue's lease: the scheduled import reads every licence holder from Graph, then
    /// writes the difference minutes later. A targeted refresh that commits in between would be overwritten by that
    /// older reading. Holding this lock from the reading to the writing makes the two take turns, on any number of
    /// importer instances - the web job is not a singleton.
    /// </para>
    /// <para>
    /// A writer checks <see cref="IsStillHeldAsync"/> immediately before each write batch. If the connection dropped,
    /// SQL Server released the lock and someone else may write: the writer must not start another batch of an older
    /// reading. Losing the lock is latched - once lost, this instance never reports it held again.
    /// </para>
    /// <para>
    /// A holder that keeps the lock through a long idle stretch - the scheduled import reads Graph for an hour on a
    /// large tenant - must call <see cref="StartKeepAlive"/>: outbound idle timeouts on App Service and the Azure SQL
    /// gateway otherwise end the session, and with it the lock, while nothing is being written.
    /// </para>
    /// <para>
    /// SqlClient's connection resiliency is switched off on this connection (<c>ConnectRetryCount = 0</c>): a
    /// transparent reconnect would carry on in a new session that does not hold the lock.
    /// </para>
    /// </remarks>
    public sealed class DirectoryWriterLock : IDisposable
    {
        /// <summary>The lock's name in <c>sp_getapplock</c>. Part of the contract between web job versions.</summary>
        public const string Resource = "AnalyticsEngine/UsersAndLicences";

        /// <summary>How often <see cref="StartKeepAlive"/> touches the session: well inside the 4-minute App Service idle timeout.</summary>
        public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromMinutes(1);

        // A SqlConnection is not thread-safe: the keep-alive and the writer's checks take turns on it.
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _keepAliveStop;
        private Task _keepAlive;
        private volatile bool _lost;

        private DirectoryWriterLock(SqlConnection connection)
        {
            Connection = connection;
        }

        /// <summary>
        /// The connection that owns the lock. Writes made on it are protected by it. Do not use it while
        /// <see cref="StartKeepAlive"/> is running: the keep-alive issues commands on it too.
        /// </summary>
        public SqlConnection Connection { get; private set; }

        /// <summary>True once the lock is known to have been lost. Never goes back to false.</summary>
        public bool IsLost => _lost;

        /// <summary>
        /// Takes the lock, waiting up to <paramref name="wait"/>; null when another writer holds it that long.
        /// </summary>
        public static async Task<DirectoryWriterLock> TryAcquireAsync(string connectionString, TimeSpan wait, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));

            // Unpooled: a pooled connection goes back to the pool with its session - and this session-owned lock - still
            // alive, so "dispose releases the lock" would only be true once the pool happened to reset it.
            // No reconnect: a silently re-established session does not hold the lock.
            var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectRetryCount = 0 };
            var connection = DataUtils.Sql.AzureSqlTokenAuth.CreateConnection(builder.ConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "sp_getapplock";
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandTimeout = (int)Math.Max(30, wait.TotalSeconds + 30);
                    command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = Resource });
                    command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
                    command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
                    command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = (int)Math.Max(0, Math.Min(int.MaxValue, wait.TotalMilliseconds)) });
                    var result = new SqlParameter("@ReturnValue", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
                    command.Parameters.Add(result);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    // 0 = granted, 1 = granted after waiting; negative = timeout, deadlock victim or error.
                    if ((int)result.Value >= 0)
                    {
                        return new DirectoryWriterLock(connection);
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

        /// <summary>
        /// Touches the session every <paramref name="interval"/> so an idle stretch cannot end it, and records a lost
        /// lock as soon as a touch finds it, rather than at the next write. <paramref name="onBeat"/> runs after each
        /// touch that found the lock held - for a holder's own heartbeat elsewhere. Best effort: its failures are ignored.
        /// </summary>
        public void StartKeepAlive(TimeSpan interval, Func<Task> onBeat = null)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (_keepAlive != null) return;

            _keepAliveStop = new CancellationTokenSource();
            var stop = _keepAliveStop.Token;
            _keepAlive = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested && !_lost)
                {
                    try
                    {
                        await Task.Delay(interval, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (!await IsStillHeldAsync().ConfigureAwait(false)) return;

                    if (onBeat != null)
                    {
                        try
                        {
                            await onBeat().ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The heartbeat is advisory; the lock itself is what was just checked.
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Stops the keep-alive and waits up to <paramref name="wait"/> (35 seconds by default) for a beat already in
        /// flight to finish, so nothing it does - refreshing a holder's heartbeat elsewhere - happens after this returns.
        /// Returns false when a beat was still running at the end of the wait: it may yet complete. The lock stays held.
        /// </summary>
        public bool StopKeepAlive(TimeSpan? wait = null)
        {
            var stop = _keepAliveStop;
            _keepAliveStop = null;
            if (stop == null) return true;

            stop.Cancel();
            var finished = true;
            try
            {
                finished = _keepAlive == null || _keepAlive.Wait(wait ?? TimeSpan.FromSeconds(35));
            }
            catch (AggregateException)
            {
                // The loop handles its own failures; nothing to add here.
            }
            _keepAlive = null;

            // A loop still running may yet look at its token: leave the source to the garbage collector.
            if (finished) stop.Dispose();
            return finished;
        }

        /// <summary>
        /// True while this session still holds the lock. False - for good - once the connection dropped (SQL Server
        /// released the lock with it) or the lock was released.
        /// </summary>
        public async Task<bool> IsStillHeldAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (_lost) return false;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_lost) return false;
                var connection = Connection;
                if (connection == null || connection.State != ConnectionState.Open)
                {
                    _lost = true;
                    return false;
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT APPLOCK_MODE('public', @resource, 'Session');";
                    command.CommandTimeout = 30;
                    command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Resource });
                    var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                    if (!string.Equals(mode, "Exclusive", StringComparison.OrdinalIgnoreCase))
                    {
                        _lost = true;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _lost = true;
            }
            finally
            {
                _gate.Release();
            }

            return !_lost;
        }

        /// <summary>Releases the lock and closes its unpooled connection; a dropped connection has already released it.</summary>
        public void Dispose()
        {
            StopKeepAlive();

            var entered = _gate.Wait(TimeSpan.FromSeconds(35));
            var connection = Connection;
            Connection = null;
            _lost = true;
            try
            {
                if (connection == null) return;
                if (connection.State == ConnectionState.Open)
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "IF APPLOCK_MODE('public', @resource, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
                        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Resource });
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception)
            {
                // Closing the session below releases it anyway.
            }
            finally
            {
                connection?.Dispose();
                if (entered) _gate.Release();
            }
        }
    }
}

using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class SqlDemoDatabase : IDisposable
    {
        internal const string Marker = "M365AnalyticsSyntheticDemo";
        internal const string FingerprintMarker = "M365AnalyticsSyntheticDemoFingerprint";
        internal const string StateMarker = "M365AnalyticsSyntheticDemoState";
        private readonly DemoOptions _options;
        private readonly CancellationToken _cancellation;
        private SqlConnection _master;
        private SqlConnection _connection;
        private bool _ready;
        public bool AlreadyComplete { get; private set; }

        /// <summary>
        /// The target's connection string: the LocalDB database named by <c>--database</c>, or the
        /// caller's own <c>--connection-string</c> (unpooled, like the LocalDB one).
        /// </summary>
        public string ConnectionString => _options.TargetConnectionString != null
            ? TargetConnection(_options.TargetConnectionString)
            : LocalConnection(_options.Database);

        /// <summary>True when the target is an existing database named by <c>--connection-string</c>.</summary>
        private bool IsConnectionStringTarget => _options.TargetConnectionString != null;

        public SqlDemoDatabase(DemoOptions options, CancellationToken cancellation)
        {
            if (options == null || options.Preview || string.IsNullOrWhiteSpace(options.Database))
                throw new ArgumentException("A parsed, non-preview demo database target is required.", nameof(options));
            _options = options;
            _cancellation = cancellation;
        }

        internal static string LocalConnection(string database) => new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = database,
            IntegratedSecurity = true,
            Pooling = false,
            ConnectTimeout = 30,
            TrustServerCertificate = true,
            ApplicationName = "Contoso synthetic demo generator"
        }.ConnectionString;

        /// <summary>
        /// A <c>--connection-string</c> target, unpooled for the same reason as <see cref="LocalConnection"/>:
        /// the session-owned applock must be released when the run's connection closes, not when a pool
        /// eventually retires it.
        /// </summary>
        internal static string TargetConnection(string connectionString) => new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = "Contoso synthetic demo generator"
        }.ConnectionString;

        public void Open(Action<string> progress)
        {
            if (_master != null || _connection != null) throw new InvalidOperationException("This demo target has already been opened.");
            if (IsConnectionStringTarget)
            {
                OpenConnectionStringTarget(progress);
                return;
            }
            _master = new SqlConnection(LocalConnection("master"));
            _master.Open();
            if (Convert.ToInt32(Scalar(_master, "SELECT CONVERT(int, SERVERPROPERTY('IsLocalDB'));")) != 1)
                throw new InvalidOperationException("Refusing a non-LocalDB server.");
            TakeApplock(_master);
            bool exists;
            using (var command = _master.CreateCommand())
            {
                command.CommandText = "SELECT DB_ID(@name);";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = _options.Database;
                exists = command.ExecuteScalar() != DBNull.Value;
            }
            if (exists && _options.Recreate)
            {
                using (var target = new SqlConnection(ConnectionString))
                {
                    target.Open();
                    EnsureResettable(target);
                }
                _cancellation.ThrowIfCancellationRequested();
                progress?.Invoke("--recreate: dropping the existing synthetic demo database " + _options.Database + "...");
                using (var command = _master.CreateCommand())
                {
                    command.CommandTimeout = 0;
                    // Database is restricted to a fixed prefix + ASCII identifier characters by the parser.
                    command.CommandText = "ALTER DATABASE [" + _options.Database + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + _options.Database + "];";
                    command.ExecuteNonQuery();
                }
                exists = false;
            }
            if (!exists)
            {
                _cancellation.ThrowIfCancellationRequested();
                using (var command = _master.CreateCommand())
                {
                    // Database is restricted to a fixed prefix + ASCII identifier characters by the parser.
                    command.CommandText = "CREATE DATABASE [" + _options.Database + "];";
                    command.ExecuteNonQuery();
                }
            }
            _connection = new SqlConnection(ConnectionString);
            _connection.Open();
            if (exists)
            {
                if (ReadProperty(_connection, Marker) != DemoOptions.FormatVersion || ReadProperty(_connection, FingerprintMarker) != _options.Fingerprint
                    || ReadProperty(_connection, StateMarker) != "complete")
                    throw new InvalidOperationException("Refusing an unmarked, incomplete or differently configured database. Choose a NEW ContosoDemo_ name, or rebuild a demo database with --recreate; no data was changed.");
                AlreadyComplete = true;
                progress?.Invoke("This exact demo generation previously completed. Read-only no-op; no schema or rows changed.");
                return;
            }
            BuildSchema(progress);
        }

        /// <summary>
        /// An existing database on any server, named by <c>--connection-string</c> - in practice the
        /// Container Apps demo's Azure SQL database. Always a <c>--recreate</c> (DemoOptions enforces it).
        /// </summary>
        /// <remarks>
        /// The database is emptied in place rather than dropped. On Azure SQL a database is an Azure
        /// resource: dropping it through T-SQL throws away its SKU (the free serverless offer and its
        /// auto-pause), and re-creating it through T-SQL gets the server's default, provisioned one. It
        /// would also drop the database users the web app and this job sign in as. Emptying it needs
        /// only db_owner on the one database, where a DROP needs server-level rights in master.
        /// </remarks>
        private void OpenConnectionStringTarget(Action<string> progress)
        {
            _connection = new SqlConnection(ConnectionString);
            OpenWithRetry(_connection, progress);
            TakeApplock(_connection);
            var objects = EnsureResettable(_connection);
            if (objects > 0)
            {
                _cancellation.ThrowIfCancellationRequested();
                progress?.Invoke($"--recreate: emptying the existing synthetic demo database {_options.Database} in place ({objects:N0} objects)...");
                // Marked as building first, so a reset that is interrupted half way can never still read as complete.
                SetProperty(StateMarker, "building");
                using (var command = _connection.CreateCommand())
                {
                    command.CommandTimeout = 0;
                    command.CommandText = ResetInPlaceSql;
                    using (_cancellation.Register(command.Cancel)) command.ExecuteNonQuery();
                }
                var left = Convert.ToInt32(Scalar(_connection, UserObjectCountSql));
                if (left != 0)
                    throw new InvalidOperationException($"--recreate left {left} objects in {_options.Database}; generation was not started.");
            }
            else progress?.Invoke($"{_options.Database} is empty; building the synthetic demo in it.");
            BuildSchema(progress);
        }

        private void BuildSchema(Action<string> progress)
        {
            SetProperty(Marker, DemoOptions.FormatVersion);
            SetProperty(FingerprintMarker, _options.Fingerprint);
            SetProperty(StateMarker, "building");
            progress?.Invoke("Applying the repository's existing schema to the new synthetic database...");
            DatabaseUpgrader.CheckDbUpgraded(new DatabaseUpgradeInfo { ConnectionString = ConnectionString },
                message => progress?.Invoke("[schema] " + message));
            _cancellation.ThrowIfCancellationRequested();
            ValidateEmptySchema();
            _ready = true;
        }

        private void TakeApplock(SqlConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"DECLARE @r int;
EXEC @r = sys.sp_getapplock @Resource=@name, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0;
SELECT @r;";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = "ContosoDemo:" + _options.Database.ToUpperInvariant();
                if (Convert.ToInt32(command.ExecuteScalar()) < 0)
                    throw new InvalidOperationException("Another generator owns this demo target; nothing was changed.");
            }
        }

        /// <summary>
        /// The <c>--recreate</c> safety check: only a database carrying this generator's marker, or a
        /// completely empty one, may be reset. Returns how many user objects it holds.
        /// </summary>
        /// <remarks>
        /// Any marker version counts, so a database left half-built by a failed or older run can still
        /// be rebuilt. What is refused is a database with objects and no marker at all - one this
        /// generator did not create, whatever its name.
        /// </remarks>
        private int EnsureResettable(SqlConnection connection)
        {
            var objects = Convert.ToInt32(Scalar(connection, UserObjectCountSql));
            if (objects > 0 && ReadProperty(connection, Marker) == null)
                throw new InvalidOperationException($"Refusing to --recreate {_options.Database}: it holds {objects:N0} objects but no synthetic demo marker, so this generator did not create it. Nothing was changed.");
            return objects;
        }

        /// <summary>
        /// Opens a connection-string target, waiting out the resume of an auto-paused Azure SQL serverless
        /// database. The first login to a paused database fails with 40613 while it starts, which takes
        /// about a minute.
        /// </summary>
        private void OpenWithRetry(SqlConnection connection, Action<string> progress)
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (true)
            {
                _cancellation.ThrowIfCancellationRequested();
                try
                {
                    connection.Open();
                    return;
                }
                catch (SqlException ex) when (Array.IndexOf(TransientConnectErrors, ex.Number) >= 0 && DateTime.UtcNow < deadline)
                {
                    progress?.Invoke($"The database is not available yet (SQL error {ex.Number}: an auto-paused Azure SQL serverless database takes about a minute to resume). Retrying in 15 seconds...");
                    _cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                }
            }
        }

        // Azure SQL's documented transient connection errors, plus the network-level ones a resuming
        // database surfaces as.
        private static readonly int[] TransientConnectErrors = { 40613, 40197, 40501, 40540, 49918, 49919, 49920, 4221, 10928, 10929, 10053, 10054, 10060, 233, 64, 121, 20 };

        /// <summary>Every user-created object, type and schema: zero for a brand-new database.</summary>
        internal const string UserObjectCountSql = @"SELECT (SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0)
     + (SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1)
     + (SELECT COUNT(*) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383)
     + (SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0);";

        /// <summary>
        /// Empties the current database back to the state of a new one, leaving the database itself, its
        /// users, roles, permissions and settings in place.
        /// </summary>
        /// <remarks>
        /// Deliberately generic rather than a list of this product's tables, so a table added by a later
        /// migration cannot survive a reset and then collide with the rebuilt schema. Foreign keys go
        /// first so tables can follow in any order; schema-bound views and functions can depend on each
        /// other in any order, so objects are dropped in passes until none are left.
        /// </remarks>
        internal const string ResetInPlaceSql = @"SET NOCOUNT ON;
SET LOCK_TIMEOUT 60000;
DECLARE @sql nvarchar(max), @pass int = 0, @remaining int = 1, @dropped int, @lastError nvarchar(2048) = N'';

SET @sql = N'';
SELECT @sql += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))
    + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';' + NCHAR(10)
FROM sys.foreign_keys WHERE is_ms_shipped = 0;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'DROP TRIGGER ' + QUOTENAME(name) + N' ON DATABASE;' + NCHAR(10)
FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' SET (SYSTEM_VERSIONING = OFF);' + NCHAR(10)
FROM sys.tables WHERE temporal_type = 2;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

WHILE @remaining > 0 AND @pass < 20
BEGIN
    SET @pass += 1;
    SET @dropped = 0;
    DECLARE drops CURSOR LOCAL FAST_FORWARD FOR
        SELECT CASE o.type
                   WHEN 'U' THEN N'DROP TABLE '
                   WHEN 'V' THEN N'DROP VIEW '
                   WHEN 'P' THEN N'DROP PROCEDURE '
                   WHEN 'PC' THEN N'DROP PROCEDURE '
                   WHEN 'SN' THEN N'DROP SYNONYM '
                   WHEN 'SO' THEN N'DROP SEQUENCE '
                   ELSE N'DROP FUNCTION '
               END + QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name)
        FROM sys.objects AS o
        WHERE o.is_ms_shipped = 0 AND o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT','SN','SO')
        ORDER BY CASE o.type WHEN 'V' THEN 0 WHEN 'P' THEN 1 WHEN 'PC' THEN 1 WHEN 'U' THEN 3 ELSE 2 END, o.object_id DESC;
    OPEN drops;
    FETCH NEXT FROM drops INTO @sql;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            EXEC sys.sp_executesql @sql;
            SET @dropped += 1;
        END TRY
        BEGIN CATCH
            SET @lastError = LEFT(@sql + N': ' + ERROR_MESSAGE(), 2048);
        END CATCH;
        FETCH NEXT FROM drops INTO @sql;
    END;
    CLOSE drops;
    DEALLOCATE drops;
    SELECT @remaining = COUNT(*) FROM sys.objects
    WHERE is_ms_shipped = 0 AND type IN ('U','V','P','PC','FN','IF','TF','FS','FT','SN','SO');
    IF @dropped = 0 BREAK;
END;
IF @remaining > 0
BEGIN
    SET @lastError = CONCAT(N'--recreate could not drop ', @remaining, N' object(s). Last error: ', @lastError);
    THROW 51001, @lastError, 1;
END;

SET @sql = N'';
SELECT @sql += N'DROP TYPE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + NCHAR(10)
FROM sys.types WHERE is_user_defined = 1;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'DROP XML SCHEMA COLLECTION ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';' + NCHAR(10)
FROM sys.xml_schema_collections WHERE schema_id <> SCHEMA_ID(N'sys');
IF @sql <> N'' EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'DROP PARTITION SCHEME ' + QUOTENAME(name) + N';' + NCHAR(10) FROM sys.partition_schemes;
SELECT @sql += N'DROP PARTITION FUNCTION ' + QUOTENAME(name) + N';' + NCHAR(10) FROM sys.partition_functions;
SELECT @sql += N'DROP FULLTEXT CATALOG ' + QUOTENAME(name) + N';' + NCHAR(10) FROM sys.fulltext_catalogs;
IF @sql <> N'' EXEC sys.sp_executesql @sql;

SET @sql = N'';
SELECT @sql += N'DROP SCHEMA ' + QUOTENAME(name) + N';' + NCHAR(10)
FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383;
IF @sql <> N'' EXEC sys.sp_executesql @sql;";

        public IDemoSink CreateSink() => _ready
            ? new SqlDemoSink(_connection, _options.BatchSize, _cancellation)
            : throw new InvalidOperationException("No writable new demo target is open.");

        private void ValidateEmptySchema()
        {
            foreach (var table in DemoTables.All)
            {
                _cancellation.ThrowIfCancellationRequested();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"SELECT c.name, c.is_nullable, c.is_identity, c.is_computed, c.default_object_id
FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(@table);";
                    command.Parameters.Add("@table", SqlDbType.NVarChar, 256).Value = "dbo." + table.Name;
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var name = reader.GetString(0);
                            columns.Add(name);
                            if (!reader.GetBoolean(1) && !reader.GetBoolean(2) && !reader.GetBoolean(3) && reader.GetInt32(4) == 0
                                && !table.Columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Generator does not supply required column dbo." + table.Name + "." + name);
                        }
                    }
                }
                if (table.Columns.Any(c => !columns.Contains(c.Name)))
                    throw new InvalidOperationException("Schema validation failed for dbo." + table.Name + "; generation was not started.");
                if (Convert.ToInt32(Scalar(_connection, "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.[" + table.Name + "]) THEN 1 ELSE 0 END;")) != 0)
                    throw new InvalidOperationException("Expected an empty new demo table: dbo." + table.Name + ". No existing rows will be reshaped.");
            }
        }

        public void ValidateAndComplete(DemoSummary summary, Action<string> progress)
        {
            if (!_ready) throw new InvalidOperationException("Only a newly created and validated demo target can be completed.");
            progress?.Invoke("Verifying persisted source-row counts...");
            foreach (var table in DemoTables.All)
            {
                _cancellation.ThrowIfCancellationRequested();
                summary.Rows.TryGetValue(table.Name, out long expected);
                if (Convert.ToInt64(Scalar(_connection, "SELECT COUNT_BIG(*) FROM dbo.[" + table.Name + "];")) != expected)
                    throw new InvalidOperationException("Persisted row count differs from the generated stream for " + table.Name);
            }
            if (_options.CompileProfiles && !_options.HasAllDailyWorkloads)
                progress?.Invoke("Weekly cross-workload profiles skipped: select all six daily M365 workloads to compile complete profiles.");
            if (_options.CompileProfiles && _options.HasAllDailyWorkloads)
            {
                var monday = _options.Start;
                while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1);
                for (; monday.AddDays(6) <= _options.ReportEnd; monday = monday.AddDays(7))
                {
                    _cancellation.ThrowIfCancellationRequested();
                    progress?.Invoke("Compiling complete Power BI reporting week " + monday.ToString("yyyy-MM-dd") + "...");
                    using (var command = _connection.CreateCommand())
                    {
                        command.CommandTimeout = 0;
                        // CompileWeekly uses GETDATE and retention cleanup. Call its date-explicit,
                        // non-retention children so fixed dates stay reproducible.
                        command.CommandText = @"EXEC profiling.usp_CompileActivityWeek @Monday;
EXEC profiling.usp_CompileUsageWeek @Monday;
-- These procedures log some failures instead of throwing: never assume ExecuteNonQuery means success.
IF (SELECT COUNT_BIG(*) FROM profiling.ActivitiesWeeklyColumns WHERE [date]=@Monday) <> @users
 OR (SELECT COUNT_BIG(*) FROM profiling.UsageWeekly WHERE [date]=@Monday) <> @users
 OR NOT EXISTS (SELECT 1 FROM profiling.ActivitiesWeekly WHERE MetricDate=@Monday)
    THROW 51000, 'Demo weekly profiling did not produce the complete user population.', 1;
IF (SELECT ISNULL(SUM([Emails Sent]),0) FROM profiling.ActivitiesWeeklyColumns WHERE [date]=@Monday)
 <> (SELECT ISNULL(SUM(email_send_count),0) FROM dbo.outlook_user_activity_log WHERE [date]>=@Monday AND [date]<DATEADD(day,7,@Monday))
    THROW 51000, 'Demo weekly profiling email totals do not match source rows.', 1;";
                        command.Parameters.Add("@Monday", SqlDbType.Date).Value = monday;
                        command.Parameters.Add("@users", SqlDbType.Int).Value = _options.Users;
                        using (_cancellation.Register(command.Cancel)) command.ExecuteNonQuery();
                    }
                    summary.CompletedProfileWeeks++;
                }
            }
            _cancellation.ThrowIfCancellationRequested();
            SetProperty(StateMarker, "complete");
            _ready = false;
        }

        private static string ReadProperty(SqlConnection connection, string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT CONVERT(nvarchar(4000),value) FROM sys.extended_properties WHERE class=0 AND name=@name;";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
                return command.ExecuteScalar() as string;
            }
        }

        private void SetProperty(string name, string value)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=@name)
EXEC sys.sp_updateextendedproperty @name=@name, @value=@value;
ELSE EXEC sys.sp_addextendedproperty @name=@name, @value=@value;";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
                command.Parameters.Add("@value", SqlDbType.NVarChar, 4000).Value = value;
                command.ExecuteNonQuery();
            }
        }

        private object Scalar(SqlConnection connection, string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = 0;
                command.CommandText = sql;
                using (_cancellation.Register(command.Cancel)) return command.ExecuteScalar();
            }
        }

        public void Dispose() { _connection?.Dispose(); _master?.Dispose(); }
    }

    internal sealed class SqlDemoSink : IDemoSink
    {
        private readonly SqlConnection _connection;
        private readonly int _batchSize;
        private readonly CancellationToken _cancellation;
        private readonly Dictionary<DemoTable, List<object[]>> _buffers = new Dictionary<DemoTable, List<object[]>>();

        public SqlDemoSink(SqlConnection connection, int batchSize, CancellationToken cancellation)
        {
            _connection = connection; _batchSize = batchSize; _cancellation = cancellation;
        }

        public void Write(DemoTable table, params object[] values)
        {
            table.ValidateValues(values);
            if (!_buffers.TryGetValue(table, out var buffer))
            {
                buffer = new List<object[]>(table.BatchLimit(_batchSize));
                _buffers.Add(table, buffer);
            }
            buffer.Add(values);
            if (buffer.Count >= table.BatchLimit(_batchSize)) Flush();
        }

        public void Flush()
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!_buffers.Values.Any(b => b.Count > 0)) return;
            using (var transaction = _connection.BeginTransaction())
            {
                foreach (var table in DemoTables.All)
                {
                    if (!_buffers.TryGetValue(table, out var rows) || rows.Count == 0) continue;
                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandTimeout = 120;
                        var sql = new StringBuilder();
                        if (table.SupplyIdentity) sql.Append("SET IDENTITY_INSERT dbo.[").Append(table.Name).Append("] ON;");
                        sql.Append("INSERT INTO dbo.[").Append(table.Name).Append("] (")
                            .Append(string.Join(",", table.Columns.Select(c => "[" + c.Name + "]"))).Append(") VALUES ");
                        int parameter = 0;
                        foreach (var row in rows)
                        {
                            if (parameter > 0) sql.Append(",");
                            sql.Append("(");
                            for (int i = 0; i < row.Length; i++)
                            {
                                if (i > 0) sql.Append(",");
                                string name = "@p" + parameter++;
                                sql.Append(name);
                                var column = table.Columns[i];
                                var p = column.Size == 0 ? command.Parameters.Add(name, column.Type)
                                    : command.Parameters.Add(name, column.Type, column.Size);
                                if (column.Precision > 0) { p.Precision = column.Precision; p.Scale = column.Scale; }
                                p.Value = row[i] ?? DBNull.Value;
                            }
                            sql.Append(")");
                        }
                        sql.Append(";");
                        if (table.SupplyIdentity) sql.Append("SET IDENTITY_INSERT dbo.[").Append(table.Name).Append("] OFF;");
                        command.CommandText = sql.ToString();
                        using (_cancellation.Register(command.Cancel)) command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
                foreach (var rows in _buffers.Values) rows.Clear();
            }
        }

        // Dispose must not flush a partial stream after an error or cancellation.
        public void Dispose() => _buffers.Clear();
    }
}

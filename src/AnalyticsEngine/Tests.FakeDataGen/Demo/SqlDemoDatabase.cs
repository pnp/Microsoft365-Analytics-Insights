using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
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
        public string ConnectionString => LocalConnection(_options.Database);

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
            ApplicationName = "Contoso synthetic demo generator"
        }.ConnectionString;

        public void Open(Action<string> progress)
        {
            if (_master != null) throw new InvalidOperationException("This demo target has already been opened.");
            _master = new SqlConnection(LocalConnection("master"));
            _master.Open();
            if (Convert.ToInt32(Scalar(_master, "SELECT CONVERT(int, SERVERPROPERTY('IsLocalDB'));")) != 1)
                throw new InvalidOperationException("Refusing a non-LocalDB server.");
            using (var command = _master.CreateCommand())
            {
                command.CommandText = @"DECLARE @r int;
EXEC @r = sys.sp_getapplock @Resource=@name, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0;
SELECT @r;";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = "ContosoDemo:" + _options.Database.ToUpperInvariant();
                if (Convert.ToInt32(command.ExecuteScalar()) < 0)
                    throw new InvalidOperationException("Another generator owns this demo target; nothing was changed.");
            }
            bool exists;
            using (var command = _master.CreateCommand())
            {
                command.CommandText = "SELECT DB_ID(@name);";
                command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = _options.Database;
                exists = command.ExecuteScalar() != DBNull.Value;
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
                if (ReadProperty(Marker) != DemoOptions.FormatVersion || ReadProperty(FingerprintMarker) != _options.Fingerprint
                    || ReadProperty(StateMarker) != "complete")
                    throw new InvalidOperationException("Refusing an unmarked, incomplete or differently configured database. Choose a NEW ContosoDemo_ name; no data was changed.");
                AlreadyComplete = true;
                progress?.Invoke("This exact demo generation previously completed. Read-only no-op; no schema or rows changed.");
                return;
            }
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
            if (_options.CompileProfiles)
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

        private string ReadProperty(string name)
        {
            using (var command = _connection.CreateCommand())
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

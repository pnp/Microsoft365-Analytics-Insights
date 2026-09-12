using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class SqlExistingDemoDatabase : IDisposable
    {
        private const string FailureAdvice = " Previously batch-committed synthetic rows may persist; no automatic cleanup is performed.";
        private static readonly Regex GuidToken = new Regex(
            @"(?<![0-9a-fA-F])(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32})(?![0-9a-fA-F])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Email = new Regex(@"^[^@\s:/?#]+@[^@\s:/?#]+$", RegexOptions.CultureInvariant);
        private readonly SqlConnection _connection;
        private readonly DemoOptions _options;
        private readonly CancellationToken _cancellation;
        private readonly byte[] _runNamespace = Guid.NewGuid().ToByteArray();
        private readonly Dictionary<DemoTable, TablePlan> _plans = new Dictionary<DemoTable, TablePlan>();
        private SHA256 _emailHash;
        private ExistingSink _sink;
        private bool _opened, _ready, _failed, _disposed, _markerInvalidated;

        /// <summary>
        /// Tables describing the TENANT rather than the appended population, so they are counted and then
        /// dropped instead of inserted. An append adds a fresh synthetic population to someone else's
        /// database; it cannot restate that tenant's Copilot totals, its Copilot Credits entitlement, its
        /// Azure bill or the fact that an import ran. Agent costs also key on (usage_date, dimension_hash),
        /// which is UNIQUE on copilot_studio_credit_user_daily: a second population regenerates the same
        /// agent/environment dimensions for the same dates, so appending them is a duplicate-key failure
        /// rather than more data.
        /// </summary>
        private static readonly DemoTable[] TenantWideTables =
        {
            DemoTables.CopilotCounts, DemoTables.StudioCredits, DemoTables.StudioUserCredits,
            DemoTables.StudioCapacity, DemoTables.AzureCosts, DemoTables.AgentCostImports,
        };

        private static bool IsTenantWide(DemoTable table) => Array.IndexOf(TenantWideTables, table) >= 0;

        public SqlExistingDemoDatabase(string connectionString, DemoOptions options, CancellationToken cancellation)
        {
            if (options == null || options.Preview || options.Help)
                throw new ArgumentException("Parsed, writable demo options are required.", nameof(options));
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("An explicit existing TEST database connection string is required.", nameof(connectionString));
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog) || IsSystemDatabase(builder.InitialCatalog)
                || !string.IsNullOrEmpty(builder.AttachDBFilename) || builder.UserInstance)
                throw new ArgumentException("Specify an existing non-system TEST catalog; attaching database files is not supported.", nameof(connectionString));
            builder.Pooling = false;
            builder.ApplicationName = "Contoso synthetic append generator";
            _connection = new SqlConnection(builder.ConnectionString);
            _options = options;
            _cancellation = cancellation;
        }

        public void Open(Action<string> progress)
        {
            if (_opened || _disposed) throw new InvalidOperationException("This existing target cannot be reopened.");
            _opened = true;
            try
            {
                _cancellation.ThrowIfCancellationRequested();
                _connection.OpenAsync(_cancellation).GetAwaiter().GetResult();
                using (var command = Command(@"SELECT DB_NAME(), DATABASEPROPERTYEX(DB_NAME(), 'Updateability');"))
                {
                    Execute(command, () =>
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read() || IsSystemDatabase(reader.GetString(0))
                                || !string.Equals(reader.GetString(0), _connection.Database, StringComparison.OrdinalIgnoreCase)
                                || !string.Equals(Convert.ToString(reader.GetValue(1)), "READ_WRITE", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("The target must be an existing writable non-system TEST database.");
                        }
                        return 0;
                    });
                }
                progress?.Invoke("Checking existing schema without creating, upgrading or reshaping application tables...");
                ReadSchema();
                ConfigureMappings();
                // These are connection-local tempdb maps, not application schema. Session/user
                // populations never accumulate in a client dictionary.
                using (var command = Command(string.Join(Environment.NewLine, _plans.Values
                    .Where(p => p.IdentityIndex >= 0).Select(p => "CREATE TABLE " + p.MapName
                        + " (logical_id int NOT NULL PRIMARY KEY, actual_id int NOT NULL);"))))
                    Execute(command, command.ExecuteNonQuery);
                _ready = true;
                progress?.Invoke("Existing-target append: fresh synthetic users; existing users and activities are never updated.");
                progress?.Invoke("Tenant-wide Copilot count snapshots, Copilot Studio credits, capacity and Azure agent spend will be skipped: this population does not represent the existing tenant.");
                if (_options.CompileProfiles)
                    progress?.Invoke("Global weekly profiling is skipped for existing targets; existing profiles are not recomputed.");
                progress?.Invoke("Batches commit independently." + FailureAdvice);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        public IDemoSink CreateSink()
        {
            EnsureReady();
            if (_sink != null) throw new InvalidOperationException("Only one append stream can be created for this run.");
            return _sink = new ExistingSink(this);
        }

        public void Complete(DemoSummary summary, Action<string> progress)
        {
            EnsureReady();
            try
            {
                _cancellation.ThrowIfCancellationRequested();
                if (summary == null || _sink == null || _sink.PendingRows != 0 || _sink.DiscardedRows)
                    throw new InvalidOperationException("Only an explicitly flushed, successful append stream can be completed.");
                foreach (var plan in _plans.Values.OrderBy(p => p.Order))
                {
                    summary.Rows.TryGetValue(plan.Table.Name, out long expected);
                    if (expected != plan.Received || plan.Received != plan.Inserted + plan.Reused + plan.Skipped)
                        throw new InvalidOperationException("Append stream accounting differs for " + plan.Table.Name + ".");
                }
                if (_plans[DemoTables.Users].Inserted != _options.Users
                    || summary.Rows.Keys.Any(n => !_plans.Keys.Any(t => t.Name == n)))
                    throw new InvalidOperationException("The requested synthetic population did not complete.");

                var inserted = _plans.Values.Sum(p => p.Inserted);
                var reused = _plans.Values.Sum(p => p.Reused);
                var skipped = _plans.Values.Sum(p => p.Skipped);
                progress?.Invoke($"Append verified from committed batches: {inserted:N0} inserted rows; {reused:N0} reused dimension rows.");
                progress?.Invoke($"Skipped {skipped:N0} tenant-wide Copilot count snapshot and agent-cost rows; no global profiling or new-demo completion marker was written.");
                summary.Rows.Clear();
                foreach (var plan in _plans.Values.Where(p => p.Received > 0))
                    summary.Rows.Add(plan.Table.Name, plan.Inserted);
                summary.CompletedProfileWeeks = 0;
                _ready = false;
            }
            catch
            {
                _failed = true;
                throw;
            }
        }

        private static bool IsSystemDatabase(string name) =>
            new[] { "master", "model", "msdb", "tempdb", "mssqlsystemresource", "distribution" }
                .Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

        private void ReadSchema()
        {
            foreach (var table in DemoTables.All)
            {
                _cancellation.ThrowIfCancellationRequested();
                var plan = new TablePlan(table, _plans.Count);
                _plans.Add(table, plan);
                using (var command = Command(@"SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length,
c.is_nullable, c.is_identity, c.is_computed, c.default_object_id, c.generated_always_type,
CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic
 ON ic.object_id=i.object_id AND ic.index_id=i.index_id
 WHERE i.object_id=c.object_id AND i.is_primary_key=1 AND ic.column_id=c.column_id AND ic.key_ordinal>0) THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id
WHERE t.object_id=OBJECT_ID(@table, 'U') AND t.is_ms_shipped=0;"))
                {
                    command.Parameters.Add("@table", SqlDbType.NVarChar, 256).Value = "dbo." + table.Name;
                    Execute(command, () =>
                    {
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                                plan.Columns.Add(reader.GetString(0), new SqlColumn
                                {
                                    Type = reader.GetString(1), Length = reader.GetInt16(2),
                                    Nullable = reader.GetBoolean(3), Identity = reader.GetBoolean(4),
                                    Computed = reader.GetBoolean(5), HasDefault = reader.GetInt32(6) != 0,
                                    Generated = reader.GetByte(7) != 0, PrimaryKey = reader.GetInt32(8) != 0
                                });
                        return 0;
                    });
                }
                if (plan.Columns.Count == 0) throw SchemaError(table, "missing table or unavailable column metadata");
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    var supplied = table.Columns[i];
                    if (!plan.Columns.TryGetValue(supplied.Name, out var actual) || !Compatible(supplied, actual)
                        || actual.Computed || actual.Generated)
                        throw SchemaError(table, "missing or incompatible supplied column " + supplied.Name);
                    if (actual.Identity)
                    {
                        if (!table.SupplyIdentity || supplied.Type != SqlDbType.Int || !actual.PrimaryKey
                            || plan.Columns.Values.Count(c => c.PrimaryKey) != 1 || plan.IdentityIndex >= 0)
                            throw SchemaError(table, "unsupported supplied identity/primary key");
                        plan.IdentityIndex = i;
                    }
                }
                if (table.SupplyIdentity && plan.IdentityIndex < 0)
                    throw SchemaError(table, "the logical integer key is not a SQL identity primary key");
                foreach (var column in plan.Columns)
                    if (!column.Value.Nullable && !column.Value.Identity && !column.Value.Computed && !column.Value.Generated
                        && !column.Value.HasDefault && column.Value.Type != "timestamp" && !table.Columns.Any(c => c.Name == column.Key))
                        throw SchemaError(table, "generator does not supply required column " + column.Key);

                using (var command = Command(@"SELECT f.object_id, pc.name, rs.name, rt.name, rc.name, f.is_disabled,
 (SELECT COUNT(*) FROM sys.foreign_key_columns x WHERE x.constraint_object_id=f.object_id)
FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
JOIN sys.columns pc ON pc.object_id=fc.parent_object_id AND pc.column_id=fc.parent_column_id
JOIN sys.tables rt ON rt.object_id=fc.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
WHERE f.parent_object_id=OBJECT_ID(@table);"))
                {
                    command.Parameters.Add("@table", SqlDbType.NVarChar, 256).Value = "dbo." + table.Name;
                    Execute(command, () =>
                    {
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                                plan.ForeignKeys.Add(new ForeignKey
                                {
                                    Column = reader.GetString(1), Schema = reader.GetString(2),
                                    Table = reader.GetString(3), TargetColumn = reader.GetString(4),
                                    Disabled = reader.GetBoolean(5), Width = reader.GetInt32(6)
                                });
                        return 0;
                    });
                }
                using (var command = Command(@"SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.triggers
 WHERE parent_id=OBJECT_ID(@table) AND is_disabled=0) THEN 1 ELSE 0 END;"))
                {
                    command.Parameters.Add("@table", SqlDbType.NVarChar, 256).Value = "dbo." + table.Name;
                    if (Convert.ToInt32(Execute(command, command.ExecuteScalar)) != 0)
                        throw SchemaError(table, "enabled triggers could modify existing rows");
                }
            }
        }

        private void ConfigureMappings()
        {
            foreach (var plan in _plans.Values)
                plan.ReuseKey = NaturalKey(plan);
            foreach (var plan in _plans.Values.OrderBy(p => p.Order))
            {
                // An unused nullable relationship must not inherit a non-NULL schema default.
                plan.NullColumns = plan.ForeignKeys.Select(f => f.Column).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => !plan.Table.Columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        && plan.Columns[name].Nullable && !plan.Columns[name].Computed
                        && !plan.Columns[name].Generated && !plan.Columns[name].Identity).ToArray();
                for (int i = 0; i < plan.Table.Columns.Count; i++)
                {
                    var supplied = plan.Table.Columns[i];
                    var keys = plan.ForeignKeys.Where(f => f.Column == supplied.Name).ToArray();
                    if (keys.Length == 0)
                    {
                        // Do not silently attach a logical ID to an existing row when a
                        // required constraint is missing. SharePoint sp_id is a remote handle.
                        if (i != plan.IdentityIndex && supplied.Type == SqlDbType.Int)
                        {
                            var inherited = InheritedIdentityReference(plan, supplied.Name);
                            if (inherited != null)
                                plan.IdentityReferences.Add(i, inherited);
                            else if (supplied.Name.EndsWith("_id", StringComparison.Ordinal)
                                && !(supplied.Name == "sp_id"
                                    && (plan.Table == DemoTables.PageComments || plan.Table == DemoTables.PageLikes)))
                                throw SchemaError(plan.Table, "missing foreign-key metadata for " + supplied.Name);
                        }
                        continue;
                    }
                    var fk = keys[0];
                    var target = _plans.Values.SingleOrDefault(p => p.Table.Name == fk.Table);
                    if (keys.Length != 1 || fk.Width != 1 || fk.Disabled || fk.Schema != "dbo" || target == null
                        || target.Order > plan.Order || IsTenantWide(target.Table))
                        throw SchemaError(plan.Table, "unsupported foreign-key mapping for " + supplied.Name);
                    int targetIndex = target.Table.Columns.ToList().FindIndex(c => c.Name == fk.TargetColumn);
                    if (targetIndex < 0 || target.Table.Columns[targetIndex].Type != supplied.Type)
                        throw SchemaError(plan.Table, "foreign key does not reference a supplied compatible key: " + supplied.Name);
                    if (supplied.Type == SqlDbType.Int)
                    {
                        if (targetIndex != target.IdentityIndex)
                            throw SchemaError(plan.Table, "integer foreign key does not reference a mapped identity: " + supplied.Name);
                        plan.IdentityReferences.Add(i, target);
                    }
                    else if ((supplied.Type != SqlDbType.UniqueIdentifier && !IsText(supplied.Type))
                        || (target.ReuseKey.Length != 0 && !target.ReuseKey.Contains(targetIndex)))
                        throw SchemaError(plan.Table, "unsupported non-identity foreign-key mapping for " + supplied.Name);
                }
            }
        }

        private TablePlan InheritedIdentityReference(TablePlan plan, string column)
        {
            // The legacy audit table has no physical user FK, even after current migrations.
            if (plan.Table == DemoTables.Audit && column == "user_id")
                return _plans[DemoTables.Users];
            if (plan.Table == DemoTables.SharePointAudit)
            {
                switch (column)
                {
                    case "url_id": return _plans[DemoTables.Urls];
                    case "file_extension_id": return _plans[DemoTables.Extensions];
                    case "file_name_id": return _plans[DemoTables.FileNames];
                    case "related_web_id": return _plans[DemoTables.Webs];
                    case "item_type_id": return _plans[DemoTables.ItemTypes];
                }
            }
            if (plan.Table != DemoTables.Chats || column != "user_id") return null;
            // The chat's denormalised user follows its shared-primary-key audit parent.
            var candidates = new HashSet<TablePlan>();
            if (plan.Columns.Values.Count(c => c.PrimaryKey) != 1) return null;
            foreach (var key in plan.ForeignKeys.Where(f => f.Width == 1 && !f.Disabled && f.Schema == "dbo"
                && plan.Columns[f.Column].PrimaryKey))
            {
                var parent = _plans.Values.SingleOrDefault(p => p.Table.Name == key.Table && p.Order < plan.Order);
                if (parent == null || !parent.Columns[key.TargetColumn].PrimaryKey) continue;
                int index = parent.Table.Columns.ToList().FindIndex(c => c.Name == column);
                if (index >= 0 && parent.IdentityReferences.TryGetValue(index, out var target))
                    candidates.Add(target);
            }
            return candidates.Count == 1 ? candidates.Single() : null;
        }

        private static int[] NaturalKey(TablePlan plan)
        {
            if (plan.IdentityIndex < 0 || plan.Table == DemoTables.Users
                || plan.ForeignKeys.Any(f => f.Table == "users" && plan.Table.Columns.Any(c => c.Name == f.Column)))
                return new int[0];
            string column = null;
            switch (plan.Table.Name)
            {
                case "copilot_ai_models":
                    return new[] { "name", "provider_name", "version" }
                        .Select(name => plan.Table.Columns.ToList().FindIndex(c => c.Name == name)).ToArray();
                case "license_types": column = "sku_id"; break;
                case "copilot_agents": column = "agent_id"; break;
                case "power_platform_connectors": column = "name"; break;
                case "sites":
                case "webs": column = "url_base"; break;
            }
            if (column != null)
                return new[] { plan.Table.Columns.ToList().FindIndex(c => c.Name == column) };
            var values = Enumerable.Range(0, plan.Table.Columns.Count).Where(i => i != plan.IdentityIndex).ToArray();
            return values.Length == 1 && IsText(plan.Table.Columns[values[0]].Type) ? values : new int[0];
        }

        private static bool IsText(SqlDbType type) => type == SqlDbType.NVarChar || type == SqlDbType.VarChar;

        private static bool Compatible(DemoColumn supplied, SqlColumn actual) =>
            Compatible(supplied, actual.Type, actual.Length);

        internal static bool Compatible(DemoColumn supplied, string actualType, int maxLengthBytes)
        {
            string expected = supplied.Type.ToString().ToLowerInvariant();
            bool typeMatches = actualType == expected
                || (supplied.Type == SqlDbType.VarChar && actualType == "nvarchar")
                || (supplied.Type == SqlDbType.DateTime && (actualType == "datetime2"
                    || (actualType == "date" && (supplied.Name == "date" || supplied.Name.EndsWith("_date", StringComparison.Ordinal)))));
            if (!typeMatches) return false;
            if (!IsText(supplied.Type)) return true;
            if (maxLengthBytes == -1) return true;
            // SQL reports bytes. ASCII varchar input can widen to Unicode, but the target's
            // character capacity must still cover the descriptor; the reverse conversion is unsafe.
            int width = maxLengthBytes / (actualType == "nvarchar" ? 2 : 1);
            return supplied.Size > 0 && supplied.Size <= width;
        }

        private static InvalidOperationException SchemaError(DemoTable table, string reason) =>
            new InvalidOperationException("Existing schema preflight refused dbo." + table.Name + ": " + reason
                + ". No application rows were written.");

        private Guid NamespaceGuid(Guid value)
        {
            var bytes = value.ToByteArray();
            for (int i = 0; i < bytes.Length; i++) bytes[i] ^= _runNamespace[i];
            return new Guid(bytes);
        }

        private object NamespaceValue(object value)
        {
            if (value is Guid guid) return NamespaceGuid(guid);
            if (!(value is string text)) return value;
            if (Email.IsMatch(text))
            {
                if (_emailHash == null) _emailHash = SHA256.Create();
                var bytes = _emailHash.ComputeHash(Encoding.UTF8.GetBytes(text.ToUpperInvariant()));
                return "demo-" + NamespaceGuid(new Guid(bytes.Take(16).ToArray())).ToString("N") + "@contoso.example";
            }
            var namespaced = GuidToken.Replace(text, match => NamespaceGuid(Guid.Parse(match.Value))
                .ToString(match.Value.Length == 32 ? "N" : "D"));
            if (Uri.TryCreate(namespaced, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                // Keep the original Unicode/escaping rather than expanding it through AbsoluteUri.
                // Row validation rejects an over-width result before SQL; identifiers are never truncated.
                int authorityEnd = namespaced.IndexOfAny(new[] { '/', '?', '#' },
                    namespaced.IndexOf("://", StringComparison.Ordinal) + 3);
                if (authorityEnd < 0) authorityEnd = namespaced.Length;
                string path = namespaced.Substring(authorityEnd);
                return namespaced.Substring(0, authorityEnd) + "/contoso-demo-" + new Guid(_runNamespace).ToString("N")
                    + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);
            }
            return namespaced;
        }

        private void Flush(ExistingSink sink)
        {
            EnsureReady();
            _cancellation.ThrowIfCancellationRequested();
            if (sink.PendingRows == 0) return;
            try
            {
                var insertedCounts = new Dictionary<TablePlan, long>();
                using (var transaction = _connection.BeginTransaction())
                {
                    if (!_markerInvalidated) InvalidateMarker(transaction);
                    foreach (var plan in _plans.Values.OrderBy(p => p.Order))
                    {
                        if (!sink.Buffers.TryGetValue(plan.Table, out var rows) || rows.Count == 0) continue;
                        using (var command = InsertCommand(plan, rows, transaction))
                            insertedCounts.Add(plan, Convert.ToInt64(Execute(command, command.ExecuteScalar)));
                    }
                    _cancellation.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                _markerInvalidated = true;
                foreach (var entry in insertedCounts)
                {
                    entry.Key.Inserted += entry.Value;
                    entry.Key.Reused += sink.Buffers[entry.Key.Table].Count - entry.Value;
                }
                sink.ClearBuffers();
            }
            catch (Exception ex)
            {
                _failed = true;
                sink.ClearBuffers();
                if (_cancellation.IsCancellationRequested)
                    throw new OperationCanceledException("Synthetic append cancelled." + FailureAdvice, ex, _cancellation);
                throw new InvalidOperationException("Synthetic append batch failed." + FailureAdvice, ex);
            }
        }

        private SqlCommand InsertCommand(TablePlan plan, List<object[]> rows, SqlTransaction transaction)
        {
            var command = Command("", transaction);
            var sql = new StringBuilder("SET NOCOUNT ON; DECLARE @inserted bigint=0, @actual int;");
            int parameterIndex = 0;
            foreach (var row in rows)
            {
                var parameters = new string[row.Length];
                for (int i = 0; i < row.Length; i++)
                {
                    string name = "@p" + parameterIndex++;
                    parameters[i] = name;
                    var column = plan.Table.Columns[i];
                    var parameter = column.Size == 0 ? command.Parameters.Add(name, column.Type)
                        : command.Parameters.Add(name, column.Type, column.Size);
                    parameter.Value = row[i] ?? DBNull.Value;
                }
                var expressions = (string[])parameters.Clone();
                foreach (var reference in plan.IdentityReferences)
                {
                    string parameter = parameters[reference.Key], map = reference.Value.MapName;
                    sql.Append("IF ").Append(parameter).Append(" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ")
                        .Append(map).Append(" WHERE logical_id=").Append(parameter)
                        .Append(") THROW 51000, 'A synthetic logical foreign key has not been mapped.', 1;");
                    expressions[reference.Key] = "(SELECT actual_id FROM " + map + " WHERE logical_id=" + parameter + ")";
                }
                if (plan.ReuseKey.Length > 0)
                {
                    sql.Append("SET @actual=NULL; SELECT TOP (1) @actual=").Append(Quote(plan.IdentityColumn))
                        .Append(" FROM ").Append(plan.SqlName).Append(" WITH (UPDLOCK,HOLDLOCK) WHERE ")
                        .Append(string.Join(" AND ", plan.ReuseKey.Select(i => "(" + Quote(plan.Table.Columns[i].Name)
                            + "=" + expressions[i] + " OR (" + Quote(plan.Table.Columns[i].Name) + " IS NULL AND "
                            + expressions[i] + " IS NULL))")))
                        .Append(" ORDER BY ").Append(Quote(plan.IdentityColumn)).Append("; IF @actual IS NULL BEGIN ");
                }
                var included = Enumerable.Range(0, row.Length).Where(i => i != plan.IdentityIndex).ToArray();
                sql.Append("INSERT INTO ").Append(plan.SqlName).Append(" (")
                    .Append(string.Join(",", included.Select(i => Quote(plan.Table.Columns[i].Name))
                        .Concat(plan.NullColumns.Select(Quote)))).Append(")");
                if (plan.IdentityIndex >= 0)
                    sql.Append(" OUTPUT ").Append(parameters[plan.IdentityIndex]).Append(",INSERTED.")
                        .Append(Quote(plan.IdentityColumn)).Append(" INTO ").Append(plan.MapName).Append(" (logical_id,actual_id)");
                sql.Append(" VALUES (").Append(string.Join(",", included.Select(i => expressions[i])
                    .Concat(plan.NullColumns.Select(name => "NULL"))))
                    .Append("); SET @inserted=@inserted+1;");
                if (plan.ReuseKey.Length > 0)
                    sql.Append(" END ELSE INSERT INTO ").Append(plan.MapName).Append(" (logical_id,actual_id) VALUES (")
                        .Append(parameters[plan.IdentityIndex]).Append(",@actual);");
            }
            sql.Append("SELECT @inserted;");
            command.CommandText = sql.ToString();
            return command;
        }

        private void InvalidateMarker(SqlTransaction transaction)
        {
            using (var command = Command(@"IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=@marker)
BEGIN
 IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=@state)
  EXEC sys.sp_updateextendedproperty @name=@state, @value=N'modified-by-append';
 IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=0 AND name=@fingerprint)
  EXEC sys.sp_updateextendedproperty @name=@fingerprint, @value=N'invalidated-by-append';
END;", transaction))
            {
                command.Parameters.Add("@marker", SqlDbType.NVarChar, 128).Value = SqlDemoDatabase.Marker;
                command.Parameters.Add("@state", SqlDbType.NVarChar, 128).Value = SqlDemoDatabase.StateMarker;
                command.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 128).Value = SqlDemoDatabase.FingerprintMarker;
                Execute(command, command.ExecuteNonQuery);
            }
        }

        private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

        private SqlCommand Command(string sql, SqlTransaction transaction = null) => new SqlCommand(sql, _connection, transaction)
        { CommandTimeout = 120 };

        private T Execute<T>(SqlCommand command, Func<T> execute)
        {
            _cancellation.ThrowIfCancellationRequested();
            using (_cancellation.Register(command.Cancel))
            {
                try { return execute(); }
                catch when (_cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Synthetic append cancelled." + FailureAdvice, _cancellation);
                }
            }
        }

        private void EnsureReady()
        {
            if (!_ready || _failed || _disposed)
                throw new InvalidOperationException("No successful writable existing-target stream is open." + FailureAdvice);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _sink?.Dispose();
            _connection.Dispose();
            _emailHash?.Dispose();
        }

        private sealed class SqlColumn
        {
            public string Type;
            public int Length;
            public bool Nullable, Identity, Computed, HasDefault, Generated, PrimaryKey;
        }

        private sealed class ForeignKey
        {
            public string Column, Schema, Table, TargetColumn;
            public int Width;
            public bool Disabled;
        }

        private sealed class TablePlan
        {
            public readonly DemoTable Table;
            public readonly int Order;
            public readonly string MapName;
            public readonly Dictionary<string, SqlColumn> Columns = new Dictionary<string, SqlColumn>(StringComparer.OrdinalIgnoreCase);
            public readonly List<ForeignKey> ForeignKeys = new List<ForeignKey>();
            public readonly Dictionary<int, TablePlan> IdentityReferences = new Dictionary<int, TablePlan>();
            public int IdentityIndex = -1;
            public int[] ReuseKey = new int[0];
            public string[] NullColumns = new string[0];
            public long Received, Inserted, Reused, Skipped;
            public string IdentityColumn => Table.Columns[IdentityIndex].Name;
            public string SqlName => "dbo." + Quote(Table.Name);
            public TablePlan(DemoTable table, int order) { Table = table; Order = order; MapName = "#ContosoDemoMap" + order; }
        }

        private sealed class ExistingSink : IDemoSink
        {
            private readonly SqlExistingDemoDatabase _owner;
            private bool _disposed;
            public readonly Dictionary<DemoTable, List<object[]>> Buffers = new Dictionary<DemoTable, List<object[]>>();
            public int PendingRows { get; private set; }
            public bool DiscardedRows { get; private set; }
            public ExistingSink(SqlExistingDemoDatabase owner) { _owner = owner; }

            public void Write(DemoTable table, params object[] values)
            {
                try
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(ExistingSink));
                    _owner.EnsureReady();
                    _owner._cancellation.ThrowIfCancellationRequested();
                    if (!_owner._plans.TryGetValue(table, out var plan))
                        throw new InvalidOperationException("The table was not preflighted.");
                    table.ValidateValues(values);
                    var row = values.Select(_owner.NamespaceValue).ToArray();
                    if (table == DemoTables.EngageGroups)
                    {
                        // Membership snapshots describe this run's population, not an existing group.
                        int name = table.Columns.ToList().FindIndex(c => c.Name == "name");
                        row[name] = row[name] + " (demo " + new Guid(_owner._runNamespace).ToString("N") + ")";
                    }
                    table.ValidateValues(row);
                    for (int i = 0; i < row.Length; i++)
                        if ((row[i] == null || row[i] == DBNull.Value) && !plan.Columns[table.Columns[i].Name].Nullable)
                            throw new InvalidOperationException("Synthetic NULL in required column " + table.Name + "." + table.Columns[i].Name);
                    if (plan.IdentityIndex >= 0 && (!(row[plan.IdentityIndex] is int key) || key <= 0))
                        throw new InvalidOperationException("A positive logical integer identity is required.");
                    plan.Received++;
                    if (IsTenantWide(table)) { plan.Skipped++; return; }
                    if (!Buffers.TryGetValue(table, out var rows))
                        Buffers.Add(table, rows = new List<object[]>(table.BatchLimit(_owner._options.BatchSize)));
                    rows.Add(row);
                    PendingRows++;
                    if (rows.Count >= table.BatchLimit(_owner._options.BatchSize)) Flush();
                }
                catch
                {
                    _owner._failed = true;
                    DiscardedRows |= PendingRows != 0;
                    ClearBuffers();
                    throw;
                }
            }

            public void Flush()
            {
                try
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(ExistingSink));
                    _owner.Flush(this);
                }
                catch
                {
                    _owner._failed = true;
                    DiscardedRows |= PendingRows != 0;
                    ClearBuffers();
                    throw;
                }
            }

            public void ClearBuffers()
            {
                foreach (var rows in Buffers.Values) rows.Clear();
                PendingRows = 0;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                DiscardedRows |= PendingRows != 0;
                ClearBuffers();
            }
        }
    }
}

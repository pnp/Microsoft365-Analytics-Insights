using Common.Entities.Config;
using DataUtils;
using DataUtils.Health;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data.Entity.Migrations.Infrastructure;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.Health
{
    public static class DefaultRuntimeHealth
    {
        private static readonly Lazy<string[]> LocalMigrations = new Lazy<string[]>(() =>
            typeof(Migrations.Configuration).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && t.Namespace == typeof(Migrations.Configuration).Namespace
                    && typeof(IMigrationMetadata).IsAssignableFrom(t))
                .Select(t => ((IMigrationMetadata)Activator.CreateInstance(t)).Id).ToArray());

        public static IDisposable Start(AppConfig config, AnalyticsLogger logger, string jobName)
        {
            if (!config.EnableDefaultHealthChecks) return null;
            return new RuntimeHealthMonitor(
                new Dictionary<HealthComponent, Func<CancellationToken, Task<RuntimeHealthResult>>>
                {
                    [HealthComponent.Sql] = token => CheckSqlAsync(config.ConnectionStrings.DatabaseConnectionString, token),
                    [HealthComponent.Credential] = token => RuntimeCredentialHealth.CheckAsync(config.TenantGUID.ToString(),
                        config.ClientID, config.ClientSecret, config.UseClientCertificate, config.KeyVaultUrl, token)
                },
                (component, result) => logger.TrackHealthCheck(component, result.Status, result.Detail,
                    result.DaysToExpiry, result.ReasonKey, jobName));
        }

        public static async Task<RuntimeHealthResult> CheckSqlAsync(string connectionString, CancellationToken cancellationToken)
        {
            var migrationConfig = new Migrations.Configuration();
            // Enumerating the build's migrations is local only: never call Update/GetPendingMigrations,
            // construct an EF runtime context, initialize a DB, or probe master from this monitor.
            var expected = LocalMigrations.Value;
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 15 };
            using (var connection = AzureSqlTokenAuth.CreateConnection(builder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = "SELECT CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Updateability'));";
                    var updateability = (string)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(updateability, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
                        return new RuntimeHealthResult(HealthStatus.Unhealthy, "SQL database is not read/write.", reasonKey: "defaultHealth.sqlReadOnly");
                    command.CommandText = "SELECT MigrationId FROM dbo.__MigrationHistory WHERE ContextKey = @contextKey;";
                    command.Parameters.AddWithValue("@contextKey", migrationConfig.ContextKey);
                    var applied = new List<string>();
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            applied.Add(reader.GetString(0));
                    return CompareSchema(expected, applied);
                }
            }
        }

        public static RuntimeHealthResult CompareSchema(IEnumerable<string> expected, IEnumerable<string> applied)
        {
            var local = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
            var database = new HashSet<string>(applied, StringComparer.OrdinalIgnoreCase);
            if (local.Count == 0 || !local.SetEquals(database))
                return new RuntimeHealthResult(HealthStatus.Unhealthy, "SQL schema differs from this build; run the matching database upgrader.", reasonKey: "defaultHealth.sqlSchemaMismatch");
            return new RuntimeHealthResult(HealthStatus.Healthy, "SQL is reachable and read/write; migration history matches this build.", reasonKey: "defaultHealth.sqlHealthy");
        }
    }
}

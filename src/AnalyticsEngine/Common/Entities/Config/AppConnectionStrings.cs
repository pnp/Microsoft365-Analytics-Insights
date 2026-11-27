using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.Config
{

    public class AppConnectionStrings
    {
        public bool TestSQLSettings(ILogger logger) => TestWebTrafficSqlOnceAsync(logger).GetAwaiter().GetResult();
        public bool TestAuditSQLSettings(ILogger logger) => TestAuditSqlOnceAsync(logger).GetAwaiter().GetResult();

        public Task<bool> TestWebTrafficSqlOnceAsync(ILogger logger, CancellationToken shutdownToken = default) =>
            ProbeAsync(logger, audit: false, shutdownToken: shutdownToken);

        public Task<bool> TestAuditSqlOnceAsync(ILogger logger, CancellationToken shutdownToken = default) =>
            ProbeAsync(logger, audit: true, shutdownToken: shutdownToken);

        /// <summary>One read-only attempt, without EF's initializer or unlimited command timeout. One deadline
        /// covers token acquisition, opening the connection and reading at most one row, even on an empty table.</summary>
        internal async Task<bool> ProbeAsync(ILogger logger, bool audit, CancellationToken shutdownToken, int timeoutSeconds = 30)
        {
            var table = audit ? "audit_events" : "hits";
            var clock = Stopwatch.StartNew();
            try
            {
                var builder = new SqlConnectionStringBuilder(DatabaseConnectionString);
                if (builder.ConnectTimeout == 0 || builder.ConnectTimeout > timeoutSeconds)
                    builder.ConnectTimeout = timeoutSeconds;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    using (var connection = AzureSqlTokenAuth.CreateConnection(builder.ConnectionString, deadline.Token))
                    {
                        await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = $"SELECT TOP (1) 1 FROM dbo.{table};";
                            command.CommandTimeout = timeoutSeconds;
                            await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
                        }
                    }
                }
                if (shutdownToken.IsCancellationRequested) return false;
                logger.LogInformation("SQL startup probe passed: table={Table}; ElapsedMs={ElapsedMs}.", table, clock.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // SqlClient can report a cancelled query as a SqlException. Only the host token, not
                // the per-attempt deadline, makes this a requested shutdown rather than an outage.
                if (shutdownToken.IsCancellationRequested) return false;
                logger.LogError(ex, "SQL startup probe failed: table={Table}; ElapsedMs={ElapsedMs}; {Chain}. Check the SQL connection string and firewall/identity access, and that the database has been upgraded.",
                    table, clock.ElapsedMilliseconds, DataUtils.ExceptionDescriber.Chain(ex));
                return false;
            }
        }

        public AppConnectionStrings() : this(name => ConfigurationManager.ConnectionStrings[name]?.ConnectionString)
        {
        }

        /// <param name="connectionStringByName">The connection string with a given name, or <c>null</c> when the
        /// configuration has none.</param>
        internal AppConnectionStrings(Func<string, string> connectionStringByName)
        {
            var dbConnectionString = connectionStringByName("SPOInsightsEntities");
            if (dbConnectionString == null)
            {
                throw new ConfigurationErrorsException("Missing SPOInsightsEntities connection string");
            }
            this.DatabaseConnectionString = dbConnectionString;

            // Service Bus is optional: only the Teams calls import needs it.
            this.ServiceBusConnectionString = connectionStringByName("ServiceBus");

            // Storage is optional too, as the Redis connection string was before it: without one (missing or empty),
            // runtime state and the audit blob checkpoint are kept in memory instead. See StateStore.IsConfigured.
            this.StorageConnectionString = connectionStringByName("Storage");
        }

        public string DatabaseConnectionString { get; set; } = null;

        // Compat with Copilot Feedback Bot
        public string SQL => DatabaseConnectionString;

        public string ServiceBusConnectionString { get; set; } = null;


        /// <summary>
        /// The solution's storage account. Besides blobs, its Table service holds the runtime state (import checkpoints,
        /// delta tokens, schedule stamps and Teams authorisation tokens) - see <see cref="State.StateStore"/>. Optional:
        /// <c>null</c> or empty means that state is kept in memory, and resets whenever the process restarts.
        /// </summary>
        public string StorageConnectionString { get; set; } = null;
    }
}

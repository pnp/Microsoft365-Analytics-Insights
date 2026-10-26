using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Common.Entities.Config
{

    public class AppConnectionStrings
    {
        /// <summary>
        /// A bounded connectivity and schema probe for the web-traffic importer: reads at most one row of <c>hits</c>
        /// (a row count would scan a table that is hundreds of millions of rows on a large tenant, on every start).
        /// </summary>
        public bool TestSQLSettings(ILogger logger) => Probe(logger, "hits", db => db.hits.Take(1).Count());

        /// <summary>The same probe for the audit importer: at most one row of <c>audit_events</c>.</summary>
        public bool TestAuditSQLSettings(ILogger logger) => Probe(logger, "audit_events", db => db.AuditEventsCommon.Take(1).Count());

        private static bool Probe(ILogger logger, string table, Func<AnalyticsEntitiesContext, int> query)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    query(db);
                }
                logger.LogInformation($"SQL startup probe passed: table={table}; ElapsedMs={clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}.");
                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // EF6 wraps a login or open failure in an EntityException; the chain carries the SQL error number.
                // Passed as template arguments, not interpolated: an exception message may contain braces.
                logger.LogError(ex, "SQL startup probe failed: table={Table}; ElapsedMs={ElapsedMs}; {Chain}. Check the SQL connection string and firewall/identity access, and that the database has been upgraded.",
                    table, clock.ElapsedMilliseconds, DataUtils.ExceptionDescriber.Chain(ex));
                return false;
            }
        }

        // net10: read through AnalyticsConfig (appsettings.json + environment variables). There is no XML
        // configuration here, so ConfigurationManager.ConnectionStrings would compile and silently find nothing.
        public AppConnectionStrings() : this(name => AnalyticsConfig.ConnectionStrings[name]?.ConnectionString)
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

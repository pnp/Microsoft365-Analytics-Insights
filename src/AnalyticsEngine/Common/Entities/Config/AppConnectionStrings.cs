using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Linq;

namespace Common.Entities.Config
{

    public class AppConnectionStrings
    {
        public bool TestSQLSettings(ILogger logger)
        {
            // Test DB conectivity
            logger.LogInformation("Testing SQL config...");

            using (var db = new AnalyticsEntitiesContext())
            {
                try
                {
                    int count = (from allHits in db.hits
                                 select allHits).Count();
                    logger.LogInformation($"Found {count.ToString("n0")} hits in table already. Test passed!");
                }
                catch (System.Data.Entity.Core.EntityException ex)
                {
                    HandleSqlTestException(ex, logger);
                    return false;
                }
                catch (Microsoft.Data.SqlClient.SqlException ex)
                {
                    HandleSqlTestException(ex, logger);
                    return false;
                }
            }

            return true;
        }


        void HandleSqlTestException(Exception ex, ILogger logger)
        {
            logger.LogInformation("Fatal error connecting to configured SQL:");
            logger.LogError(ex, ex.Message);

            Console.WriteLine("Check your SQL configuration in the .config file / App Service settings.");
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

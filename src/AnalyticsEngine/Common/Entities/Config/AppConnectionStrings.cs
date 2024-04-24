using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Linq;

namespace Common.Entities.Config
{

    public class AppConnectionStrings
    {
        public bool TestSQLSettings(ILogger debugTracer)
        {
            // Test DB conectivity
            debugTracer.LogInformation("Testing SQL config...");

            using (var db = new AnalyticsEntitiesContext())
            {
                try
                {
                    int count = (from allHits in db.hits
                                 select allHits).Count();
                    debugTracer.LogInformation($"Found {count.ToString("n0")} hits in table already. Test passed!");
                }
                catch (System.Data.Entity.Core.EntityException ex)
                {
                    HandleSqlTestException(ex, debugTracer);
                    return false;
                }
                catch (System.Data.SqlClient.SqlException ex)
                {
                    HandleSqlTestException(ex, debugTracer);
                    return false;
                }
            }

            return true;
        }


        void HandleSqlTestException(Exception ex, ILogger debugTracer)
        {
            debugTracer.LogInformation("Fatal error connecting to configured SQL:");
            debugTracer.LogError(ex, ex.Message);

            Console.WriteLine("Check your SQL configuration in the .config file / App Service settings.");
        }

        public AppConnectionStrings()
        {
            var dbConnectionString = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"];
            if (dbConnectionString == null)
            {
                throw new ConfigurationErrorsException("Missing SPOInsightsEntities connection string");
            }
            this.DatabaseConnectionString = dbConnectionString.ConnectionString;

            var redisConnectionString = ConfigurationManager.ConnectionStrings["Redis"];
            if (redisConnectionString == null)
            {
                throw new ConfigurationErrorsException("Missing redis connection string");
            }
            this.RedisConnectionString = redisConnectionString.ConnectionString;

            var sb = ConfigurationManager.ConnectionStrings["ServiceBus"];
            if (sb == null)
            {
                throw new ConfigurationErrorsException("Missing ServiceBus connection string");
            }
            else
            {
                this.ServiceBusConnectionString = sb.ConnectionString;
            }

            var storageConfig = ConfigurationManager.ConnectionStrings["Storage"];
            if (storageConfig == null)
            {
                throw new ConfigurationErrorsException("Missing storage connection string");
            }
            else
            {
                this.StorageConnectionString = storageConfig.ConnectionString;
            }
        }

        public string DatabaseConnectionString { get; set; } = null;

        public string RedisConnectionString { get; set; } = null;

        public string ServiceBusConnectionString { get; set; } = null;


        public string StorageConnectionString { get; set; } = null;
    }
}

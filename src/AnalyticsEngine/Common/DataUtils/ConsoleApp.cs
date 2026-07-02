using Microsoft.Extensions.Logging;
using System;

namespace DataUtils
{
    /// <summary>
    /// Provides utilities for console apps.
    /// </summary>
    public class ConsoleApp
    {

        public static void WebjobWait(ILogger logger)
        {
            logger.LogInformation("Waiting 10 mins...");
            System.Threading.Thread.Sleep(600000); // 10 mins
        }

        /// <summary>
        /// Exit app. 
        /// </summary>
        public static void BombOut(bool error)
        {
#if DEBUG
            Console.WriteLine("\nDEBUG MODE: All done. Press any key to continue.");
            Console.ReadKey();
#endif
            if (error)
            {
                Environment.Exit(-1);
            }
            else
            {
                Environment.Exit(0);
            }
        }

        public static void PrintStartupAndLoggingConfig(string efConnectionString, string buildLabel, string userGroupsFilterString, ILogger logger)
        {

            logger.LogInformation($"Office 365 Advanced Analytics engine START: '{buildLabel}'.");
            var sqlConnectionInfo = new System.Data.SqlClient.SqlConnectionStringBuilder(efConnectionString);
            logger.LogInformation($"Destination SQL Server='{sqlConnectionInfo.DataSource}', DB='{sqlConnectionInfo.InitialCatalog}'.");
            if (!string.IsNullOrEmpty(userGroupsFilterString))
            {
                logger.LogWarning($"WARNING: User groups import filter configured: '{userGroupsFilterString}'. Will not import data for users not in those groups");
            }
        }
    }
}

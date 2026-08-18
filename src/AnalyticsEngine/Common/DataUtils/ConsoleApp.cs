using Microsoft.Extensions.Logging;
using System;

namespace DataUtils
{
    /// <summary>
    /// Provides utilities for console apps.
    /// </summary>
    public class ConsoleApp
    {

        /// <summary>
        /// Pause between WebJob import cycles.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="waitMinutes">Minutes to wait. Clamped to a minimum of 1. Default 10.</param>
        public static void WebjobWait(ILogger logger, int waitMinutes = 10)
        {
            var minutes = waitMinutes < 1 ? 1 : waitMinutes;
            logger.LogInformation($"Waiting {minutes} min(s)...");
            System.Threading.Thread.Sleep(minutes * 60 * 1000);
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

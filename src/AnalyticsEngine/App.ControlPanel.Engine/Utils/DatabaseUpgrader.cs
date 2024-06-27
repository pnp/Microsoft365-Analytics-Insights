using App.ControlPanel.Engine.Models;
using Common.Entities;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Single point of entry for database upgrade logic.
    /// </summary>
    public class DatabaseUpgrader
    {
        const string SqlResourceNameStart = "App.ControlPanel.Engine.SqlExtentions";

        public static void CheckDbUpgraded(DatabaseUpgradeInfo initInfo, Action<string> log)
        {
            var thisAsembly = System.Reflection.Assembly.GetEntryAssembly();
            var buildLabel = System.Configuration.ConfigurationManager.AppSettings["BuildLabel"] ?? "Unknown build";

            log?.Invoke($"Build '{buildLabel}' - begin database upgrade.");
            log?.Invoke($"[{DateTime.Now}]: Connecting to database @ '{initInfo.ConnectionString}' with Entity Framework context initializer set to 'MigrateDatabaseToLatestVersion'...");

            // Update schema with EF migration
            try
            {
                using (var context = new AnalyticsEntitiesContext(initInfo.ConnectionString, true, true))
                {
                    log?.Invoke($"--Initializing context...");
                    context.Database.Initialize(true);

                    // If we're here, the DB schema is up to date. Read something just to be sure.
                    log?.Invoke($"--Reading config table...");
                    var configCounts = context.ConfigStates.Count();
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Initialise database failed with EF. Exception: '{ex}'.");
                throw;
            }

            // Run custom SQL scripts
            var rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            try
            {
                using (var context = new AnalyticsEntitiesContext(initInfo.ConnectionString, true, false))
                {
                    var sqlScriptNames = rr.GetResourceNamesMatchingPathRoot(SqlResourceNameStart);
                    sqlScriptNames.Sort();
                    foreach (var scriptName in sqlScriptNames)
                    {
                        log?.Invoke($"--Running script '{scriptName}'...");
                        var script = rr.ReadResourceString(scriptName);

                        var statements = SplitSqlStatements(script);
                        foreach (var statement in statements)
                            context.Database.ExecuteSqlCommand(statement);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Run custom SQL scripts failed. Exception: '{ex}'.");
                throw;
            }

            // Insert org URLs
            try
            {
                using (var context = new AnalyticsEntitiesContext(initInfo.ConnectionString, true, true))
                {
                    initInfo.EnsureOrgURLs(context);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Org URL population check failed. Exception: '{ex}'.");
                return;
            }

            // Done
            log?.Invoke($"[{DateTime.Now}]: Database initialised successfully. Everything worked.");
        }

        // https://stackoverflow.com/questions/18596876/go-statements-blowing-up-sql-execution-in-net
        private static IEnumerable<string> SplitSqlStatements(string sqlScript)
        {
            // Make line endings standard to match RegexOptions.Multiline
            sqlScript = Regex.Replace(sqlScript, @"(\r\n|\n\r|\n|\r)", "\n");

            // Split by "GO" statements
            var statements = Regex.Split(
                    sqlScript,
                    @"^[\t ]*GO[\t ]*\d*[\t ]*(?:--.*)?$",
                    RegexOptions.Multiline |
                    RegexOptions.IgnorePatternWhitespace |
                    RegexOptions.IgnoreCase);

            // Remove empties, trim, and return
            return statements
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim(' ', '\n'));
        }
    }
}

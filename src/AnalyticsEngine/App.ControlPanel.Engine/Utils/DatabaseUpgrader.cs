using App.ControlPanel.Engine.Models;
using Common.Entities;
using DataUtils;
using DataUtils.Sql;
using System;
using System.Linq;

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
            var buildLabel = Common.Entities.BuildConstants.BuildLabel;

            log?.Invoke($"Build '{buildLabel}' - begin database upgrade.");

            // A connection string with no user id means the server has SQL authentication disabled, so the
            // upgrade has to authenticate with Microsoft Entra ID. The caller supplies the service principal
            // to use (see DatabaseUpgradeInfo); registering it here covers both the EF migration pipeline
            // (via the connection interceptor) and the raw ADO.NET script steps below. See issue #117.
            if (AzureSqlTokenAuth.NeedsAccessToken(initInfo.ConnectionString))
            {
                if (initInfo.HasEntraCredential)
                {
                    log?.Invoke("Authenticating to Azure SQL with Microsoft Entra ID (no SQL login in the connection string).");
                    AzureSqlTokenAuth.SetCredential(new Azure.Identity.ClientSecretCredential(
                        initInfo.EntraTenantId, initInfo.EntraClientId, initInfo.EntraClientSecret));
                }
                else
                {
                    log?.Invoke(
                        "WARNING: the connection string has no SQL login, so Microsoft Entra ID authentication is required, " +
                        "but no credential was supplied. This usually means the installer that launched this upgrade is older " +
                        "than this build. Re-run the upgrade with a matching installer.");
                }
            }

            log?.Invoke($"[{DateTime.Now}]: Connecting to database @ '{StringUtils.RedactSqlConnectionString(initInfo.ConnectionString)}' with Entity Framework context initializer set to 'MigrateDatabaseToLatestVersion'...");

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

                        var statements = StringUtils.SplitSqlStatements(script);
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

    }
}

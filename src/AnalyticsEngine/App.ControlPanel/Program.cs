using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using DataUtils;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace App.ControlPanel
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// "--initdb $connectionString" for upgrading DBs
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Are we running a special operation instead of just opening the UI? 
            // We can init the SQL database with EF (upgrade the schema), or register an install state in the local DB.
            // This happens as part of the install process, where the downloaded installer app is launched with special args.
            // 1st arg is the operation, 2nd arg is the param.
            if (args != null && args.Length > 1)
            {
                // If there's a space in the param string, C# splits the string into seperate args. Concat them again
                var secondArgfullArgsString = string.Empty;
                for (int i = 1; i < args.Length; i++) secondArgfullArgsString += args[i] + " ";

                // What special operation are we trying to do? 
                if (args.Contains(InstallerConstants.PARAM_INITDB))
                {
                    // Assume we've been passed the Base64 json for DatabaseUpgradeInfo (connection string)
                    DatabaseUpgradeInfo initInfo = null;
                    try
                    {
                        initInfo = DatabaseUpgradeInfo.GetFromBase64String(secondArgfullArgsString);
                    }
                    catch (FormatException)
                    {
                        AddToWindowsEventLog("Couldn't convert init param from base64; assuming the connection-string was sent in clear-text.");
                        initInfo = new DatabaseUpgradeInfo() { ConnectionString = secondArgfullArgsString };
                    }

                    // Attempt to upgrade/verify the DB is on the correct migration
                    DatabaseUpgrader.CheckDbUpgraded(initInfo, msg => AddToWindowsEventLog(msg));
                    return;
                }
                else if (args.Contains(InstallerConstants.PARAM_REGISTERCONFIG))
                {
                    // We want to register an install state in the local DB. This is used for logging install operations.
                    // We should've been passed the Base64 json for a temp file containing the config we want
                    var fileName = string.Empty;
                    try
                    {
                        fileName = secondArgfullArgsString.Base64Decode();
                    }
                    catch (FormatException)
                    {
                        AddToWindowsEventLog($"Couldn't convert init param from base64 value '{secondArgfullArgsString}'. Aborting registering config.", true);
                        return;
                    }

                    if (!File.Exists(fileName))
                    {
                        AddToWindowsEventLog($"Couldn't open file '{fileName}'. Aborting registering config.", true);
                        return;
                    }

                    InstallStatus installConfigAndEvents = null;
                    try
                    {
                        installConfigAndEvents = InstallStatus.GetFromBase64String(File.ReadAllText(fileName));
                    }
                    catch (FormatException ex)
                    {
                        AddToWindowsEventLog($"Couldn't parse file data in '{fileName}': {ex.Message}. Aborting registering config.", true);
                    }

                    if (installConfigAndEvents != null)
                    {
                        RegisterInstallConfigAndEvents(installConfigAndEvents);
                    }

                    // Clean-up temp file
                    try
                    {
                        File.Delete(fileName);
                    }
                    catch (Exception)
                    {
                        // Whatevs
                    }

                    return;
                }
                else
                {
                    MessageBox.Show("App launched with unexpected start-up parameters. Try updating this application.",
                        "Office 365 Advanced Analytics Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // Nothing special to do. Load app UI.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        /// <summary>
        /// Register a configuration state in local DB. Used for logging install operations.
        /// </summary>
        private static void RegisterInstallConfigAndEvents(InstallStatus initInfo)
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext(initInfo.ConnectionString, true, false))
                {
                    // If we're here, EF has successfully updated/verified the DB. Now register the config and events involved in the install.
                    db.ConfigStates.Add(new Common.Entities.Config.ConfigState
                    {
                        InstalledByUser = initInfo.SetupUserName,
                        DateApplied = DateTime.Now,
                        Messages = initInfo.Events.ToSingleString(),
                        ConfigJson = initInfo.ConfigurationJSon
                    });

                    db.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                AddToWindowsEventLog($"Registering config failed. Exception: '{ex}'.");
                throw;
            }

            AddToWindowsEventLog($"Configuration registered successfully.");
        }

        static void AddToWindowsEventLog(string msg)
        {
            AddToWindowsEventLog(msg, false);
        }
        static void AddToWindowsEventLog(string msg, bool isError)
        {
            var buildLabel = System.Configuration.ConfigurationManager.AppSettings["BuildLabel"] ?? "Unknown build";
            using (var eventLog = new EventLog("Application"))
            {
                eventLog.Source = "Application";
                Console.WriteLine(msg);
                eventLog.WriteEntry($"{buildLabel} - {msg}", isError ? EventLogEntryType.Error : EventLogEntryType.Information, InstallerConstants.EVENT_LOG_CATEGORY_ID, 1);
            }
        }
    }
}

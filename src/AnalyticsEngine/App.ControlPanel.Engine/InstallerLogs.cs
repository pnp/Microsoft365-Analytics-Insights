using System;
using System.Diagnostics;

namespace App.ControlPanel.Engine
{
    public class InstallerLogs
    {

        public static void AddToWindowsEventLog(string msg)
        {
            AddToWindowsEventLog(msg, false);
        }
        public static void AddToWindowsEventLog(string msg, bool isError)
        {
            var buildLabel = Common.Entities.BuildConstants.BuildLabel;
            Console.WriteLine(msg);
            try
            {
                using (var eventLog = new EventLog("Application"))
                {
                    eventLog.Source = "Application";
                    eventLog.WriteEntry($"{buildLabel} - {msg}", isError ? EventLogEntryType.Error : EventLogEntryType.Information, InstallerConstants.EVENT_LOG_CATEGORY_ID, 1);
                }
            }
            catch
            {
                // Windows Event Log is not available in all hosting environments (e.g. App Service
                // web-jobs run under a restricted identity). Console output already happened above.
            }
        }
    }
}

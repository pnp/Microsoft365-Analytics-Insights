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
            using (var eventLog = new EventLog("Application"))
            {
                eventLog.Source = "Application";
                Console.WriteLine(msg);
                eventLog.WriteEntry($"{buildLabel} - {msg}", isError ? EventLogEntryType.Error : EventLogEntryType.Information, InstallerConstants.EVENT_LOG_CATEGORY_ID, 1);
            }
        }
    }
}

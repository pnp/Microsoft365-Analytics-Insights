using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

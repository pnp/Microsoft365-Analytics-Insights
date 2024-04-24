using System;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.Models
{

    /// <summary>
    /// Logging event arguments
    /// </summary>
    public class InstallLogEventArgs : EventArgs
    {
        public string Text { get; set; }
        public bool IsError { get; set; }
    }
    public static class InstallEventsExtensions
    {
        public static string ToSingleString(this List<InstallLogEventArgs> installLogEvents)
        {
            string s = string.Empty;
            foreach (var e in installLogEvents)
            {
                if (e.IsError)
                {
                    s += $"[ERROR]\t{e.Text}" + Environment.NewLine;
                }
                else
                {
                    s += $"[INFO]\t{e.Text}" + Environment.NewLine;
                }
            }

            return s.TrimEnd(Environment.NewLine.ToCharArray());
        }
    }
}

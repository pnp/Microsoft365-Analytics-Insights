using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    /// <summary>
    /// Global trace configuration for capturing raw audit log JSON bodies to disk - all of them, or only those
    /// that contain a specific email address. Populated by Program argument parsing in the WebJob project.
    /// </summary>
    public static class AuditTraceConfig
    {
        /// <summary>
        /// Email address to search for within individual audit log JSON items. When set, only the items that
        /// contain it are written; when not, a trace directory on its own writes every item.
        /// </summary>
        public static string TraceEmail { get; set; } = null;
        /// <summary>
        /// Directory to write matching JSON items to. Must exist / be creatable. Nothing is written without it.
        /// </summary>
        public static string TraceDirectory { get; set; } = null;

        /// <summary>
        /// Whether one raw audit record should be written to <see cref="TraceDirectory"/>.
        /// </summary>
        internal static bool ShouldTrace(JObject record)
        {
            return ShouldTrace(record, TraceDirectory, TraceEmail);
        }

        internal static bool ShouldTrace(JObject record, string traceDirectory, string traceEmail)
        {
            if (record == null || string.IsNullOrWhiteSpace(traceDirectory)) return false;

            // Directory only: the documented "dump every record" mode.
            if (string.IsNullOrWhiteSpace(traceEmail)) return true;

            var email = traceEmail.Trim();
            return record.Descendants()
                .OfType<JValue>()
                .Any(value => value.Type == JTokenType.String && ContainsAddress((string)value.Value, email));
        }

        /// <summary>
        /// Case-insensitive search for <paramref name="email"/> as a whole address anywhere in
        /// <paramref name="value"/> - a user id, a recipient list, a URL. "Whole" so that tracing
        /// ann@contoso.com does not also write out the records of joann@contoso.com or ann@contoso.com.au.
        /// </summary>
        internal static bool ContainsAddress(string value, string email)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(email)) return false;

            var start = value.IndexOf(email, StringComparison.OrdinalIgnoreCase);
            while (start >= 0)
            {
                var end = start + email.Length;
                var startsAddress = start == 0 || !IsAddressCharacter(value[start - 1]);
                var endsAddress = end == value.Length || !ContinuesAddress(value, end);
                if (startsAddress && endsAddress) return true;

                start = value.IndexOf(email, start + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsAddressCharacter(char c)
        {
            return char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '%' || c == '+' || c == '-';
        }

        // A full stop ends the address unless more of a domain follows it (".au").
        private static bool ContinuesAddress(string value, int index)
        {
            var c = value[index];
            if (c == '.') return index + 1 < value.Length && char.IsLetterOrDigit(value[index + 1]);
            return IsAddressCharacter(c);
        }
    }
}

using System;
using System.Text.RegularExpressions;

namespace DataUtils.Sql
{
    /// <summary>
    /// SQL Server writes the offending value into some error messages: the value that would be truncated
    /// (2628), the duplicate key (2601, 2627, and 1505 when a unique index is built) and the value that failed a
    /// conversion (245). That value is tenant data - a URL, a file or office name - so remove it before a message
    /// is logged. <see cref="AnalyticsLogger"/> applies this to every line and exception it logs (#853).
    /// </summary>
    public static class SqlErrorMessages
    {
        private const RegexOptions Options = RegexOptions.Multiline | RegexOptions.CultureInvariant;

        private static readonly Regex TruncatedValue = new Regex(@"(Truncated value: )[^\r\n]*", Options);
        private static readonly Regex DuplicateKeyValue = new Regex(@"(The duplicate key value is )\([^\r\n]*\)", Options);
        private static readonly Regex ConvertedValue = new Regex(@"(when converting the \w+ value )'[^\r\n]*'(?= to data type)", Options);

        /// <summary>The message with any value SQL Server quoted in it replaced by &lt;redacted&gt;.</summary>
        public static string WithoutValues(string message)
        {
            // Every pattern contains "value", and almost no log line does: one scan instead of three.
            if (string.IsNullOrEmpty(message) || message.IndexOf("value", StringComparison.Ordinal) < 0) return message;

            var redacted = TruncatedValue.Replace(message, "$1<redacted>.");
            redacted = DuplicateKeyValue.Replace(redacted, "$1(<redacted>)");
            return ConvertedValue.Replace(redacted, "$1'<redacted>'");
        }
    }
}

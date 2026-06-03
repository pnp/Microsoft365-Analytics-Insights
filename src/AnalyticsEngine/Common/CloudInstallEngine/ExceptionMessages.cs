using System;
using System.Text;

namespace CloudInstallEngine
{
    /// <summary>
    /// Formats exceptions for installer logs so the inner-exception chain isn't lost when
    /// only <see cref="Exception.Message"/> is written to the log (which is what the custom
    /// installer <see cref="Microsoft.Extensions.Logging.ILogger"/> implementation does).
    /// </summary>
    public static class ExceptionMessages
    {
        /// <summary>
        /// Returns the exception message followed by every inner-exception message in the chain,
        /// one per line, indented. <see cref="AggregateException.InnerExceptions"/> are unrolled.
        /// </summary>
        public static string Format(Exception ex)
        {
            if (ex == null) return string.Empty;

            var sb = new StringBuilder();
            AppendChain(sb, ex, depth: 0);
            return sb.ToString().TrimEnd();
        }

        private static void AppendChain(StringBuilder sb, Exception ex, int depth)
        {
            if (ex == null) return;

            var indent = depth == 0 ? string.Empty : new string(' ', depth * 2) + "↳ ";
            sb.Append(indent).Append('[').Append(ex.GetType().Name).Append("] ").AppendLine(ex.Message);

            if (ex is AggregateException agg && agg.InnerExceptions != null)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    AppendChain(sb, inner, depth + 1);
                }
                return;
            }

            if (ex.InnerException != null)
            {
                AppendChain(sb, ex.InnerException, depth + 1);
            }
        }
    }
}

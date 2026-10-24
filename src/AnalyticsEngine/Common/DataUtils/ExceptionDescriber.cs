using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DataUtils
{
    /// <summary>
    /// Turns an exception into the text and the dimensions a log export needs to say what failed: the type of
    /// every exception in the chain, the SQL error number and the HTTP status. EF6 wraps login and open failures
    /// in an <c>EntityException</c> and async code wraps everything in an <c>AggregateException</c>, so the outer
    /// type and message on their own say very little.
    /// </summary>
    public static class ExceptionDescriber
    {
        private const int MaxChainDepth = 12;

        /// <summary>
        /// One line: <c>Type: message ---&gt; Type: message</c>, outermost first, with the SQL error number after any
        /// SQL exception. Never contains a stack trace.
        /// </summary>
        public static string Chain(Exception ex)
        {
            if (ex == null) return string.Empty;
            return string.Join(" ---> ", Flatten(ex).Select(DescribeOne));
        }

        /// <summary>The first SQL Server error number found anywhere in the exception chain, if any.</summary>
        public static int? FindSqlErrorNumber(Exception ex)
        {
            foreach (var e in Flatten(ex))
            {
                if (e is Microsoft.Data.SqlClient.SqlException sql) return sql.Number;
            }
            return null;
        }

        /// <summary>
        /// The first HTTP status carried by an exception in the chain, found by a conventional property
        /// (<c>ResponseStatusCode</c> on Graph's <c>ODataError</c>, <c>StatusCode</c> elsewhere) so this assembly does
        /// not need a reference to every SDK.
        /// </summary>
        public static int? FindHttpStatus(Exception ex)
        {
            foreach (var e in Flatten(ex))
            {
                foreach (var name in new[] { "ResponseStatusCode", "StatusCode" })
                {
                    var value = e.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(e);
                    if (value is int i && i >= 100 && i < 600) return i;
                    if (value is Enum en)
                    {
                        var numeric = Convert.ToInt32(en, CultureInfo.InvariantCulture);
                        if (numeric >= 100 && numeric < 600) return numeric;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Stable dimensions that classify the failure, for telemetry: the outermost and innermost exception type,
        /// and the SQL error number and HTTP status when present. Contains no message text.
        /// </summary>
        public static Dictionary<string, string> Dimensions(Exception ex)
        {
            var dimensions = new Dictionary<string, string>();
            if (ex == null) return dimensions;
            dimensions["ExceptionType"] = ex.GetType().Name;
            var root = Flatten(ex).Last();
            if (!ReferenceEquals(root, ex)) dimensions["RootExceptionType"] = root.GetType().Name;
            var sqlNumber = FindSqlErrorNumber(ex);
            if (sqlNumber.HasValue) dimensions["SqlErrorNumber"] = sqlNumber.Value.ToString(CultureInfo.InvariantCulture);
            var http = FindHttpStatus(ex);
            if (http.HasValue) dimensions["HttpStatus"] = http.Value.ToString(CultureInfo.InvariantCulture);
            return dimensions;
        }

        /// <summary>
        /// The block a console line adds when an exception accompanies it: the chain, and for an error the full
        /// stack as well. Each line starts with a space so it reads as a continuation of the log line.
        /// </summary>
        public static string ConsoleDetail(Exception ex, bool includeStack)
        {
            if (ex == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append(" Exception: ").Append(Chain(ex));
            if (includeStack)
            {
                sb.AppendLine();
                sb.Append(ex.ToString());
            }
            return sb.ToString();
        }

        /// <summary>The outermost exception first, then every inner exception depth-first, bounded.</summary>
        private static List<Exception> Flatten(Exception ex)
        {
            var result = new List<Exception>();
            var current = ex;
            while (current != null && result.Count < MaxChainDepth)
            {
                result.Add(current);
                if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 1)
                {
                    foreach (var inner in aggregate.InnerExceptions.Skip(1))
                    {
                        if (result.Count >= MaxChainDepth) break;
                        result.AddRange(Flatten(inner).Take(MaxChainDepth - result.Count));
                    }
                }
                current = current.InnerException;
            }
            return result;
        }

        private static string DescribeOne(Exception e)
        {
            var text = e.GetType().Name + ": " + e.Message;
            if (e is Microsoft.Data.SqlClient.SqlException sql)
            {
                text += " (SQL error " + sql.Number.ToString(CultureInfo.InvariantCulture) + ")";
            }
            return text;
        }
    }
}

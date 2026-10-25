using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using System;
using System.Collections;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// A short, stable, log-safe name for why something failed: the HTTP status and Graph error code, the SQL error number,
    /// or the exception type. Never the exception's message, which can carry URLs, UPNs or row values. Used to group
    /// failures in a summary line, so an export of the logs shows at a glance what is failing.
    /// </summary>
    internal static class FailureClassification
    {
        public static string Describe(Exception ex)
        {
            ex = Unwrap(ex);
            if (ex == null)
            {
                return "UnknownException";
            }

            if (ex is ApiException api && api.ResponseStatusCode > 0)
            {
                var code = (api as ODataError)?.Error?.Code;
                return string.IsNullOrWhiteSpace(code) ? $"HTTP {api.ResponseStatusCode}" : $"HTTP {api.ResponseStatusCode} {code}";
            }

            if (ex is GraphHttpException graph)
            {
                var code = string.IsNullOrWhiteSpace(graph.GraphErrorCode) ? graph.StatusCode.ToString() : graph.GraphErrorCode;
                return $"HTTP {(int)graph.StatusCode} {code}";
            }

            var sqlNumber = SqlErrorNumber(ex);
            return sqlNumber.HasValue ? $"SQL {sqlNumber.Value}" : ex.GetType().Name;
        }

        /// <summary>The exception a wrapper (a channel-read failure, an aggregate of one) is reporting.</summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex != null)
            {
                if (ex is Teams.ChannelMessagesReadException && ex.InnerException != null)
                {
                    ex = ex.InnerException;
                }
                else if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                {
                    ex = aggregate.InnerExceptions[0];
                }
                else
                {
                    break;
                }
            }

            return ex;
        }

        /// <summary>The number of the first SQL Server error in the exception chain, or null when there is none.</summary>
        public static int? SqlErrorNumber(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (!current.GetType().Name.EndsWith("SqlException", StringComparison.Ordinal))
                {
                    continue;
                }

                var number = current.GetType().GetProperty("Number");
                if (number != null && number.PropertyType == typeof(int))
                {
                    return (int)number.GetValue(current);
                }

                var errors = current.GetType().GetProperty("Errors")?.GetValue(current) as IEnumerable;
                if (errors != null)
                {
                    foreach (var error in errors)
                    {
                        var errorNumber = error.GetType().GetProperty("Number");
                        if (errorNumber != null && errorNumber.PropertyType == typeof(int))
                        {
                            return (int)errorNumber.GetValue(error);
                        }
                    }
                }
            }

            return null;
        }
    }
}

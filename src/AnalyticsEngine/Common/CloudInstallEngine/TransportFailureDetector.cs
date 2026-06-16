using Azure;
using System;
using System.Collections.Generic;

namespace CloudInstallEngine
{
    /// <summary>
    /// Classifies whether an exception is a network transport / DNS resolution failure (the cause of
    /// "The remote name could not be resolved: '&lt;x&gt;.vault.azure.net'" install failures) as opposed to
    /// an HTTP error response from the service.
    /// <para>
    /// Azure.Core surfaces transport failures as a <see cref="RequestFailedException"/> with
    /// <c>Status == 0</c> wrapping a <see cref="System.Net.WebException"/> /
    /// <see cref="System.Net.Http.HttpRequestException"/> / <see cref="System.Net.Sockets.SocketException"/>,
    /// and the retry policy rethrows them inside an <see cref="AggregateException"/>.
    /// </para>
    /// </summary>
    public static class TransportFailureDetector
    {
        /// <summary>
        /// True when <paramref name="ex"/> (or any exception nested within it, including
        /// <see cref="AggregateException.InnerExceptions"/>) is a network transport / DNS resolution failure.
        /// <paramref name="leafMessage"/> receives the innermost (most specific) matching message.
        /// </summary>
        public static bool IsTransportOrDnsFailure(Exception ex, out string leafMessage)
        {
            leafMessage = null;
            foreach (var node in Flatten(ex))
            {
                if (node is System.Net.Sockets.SocketException
                    || node is System.Net.WebException
                    || node is System.Net.Http.HttpRequestException
                    || (node is RequestFailedException rfe && rfe.Status == 0))
                {
                    // Keep walking so the innermost (most specific) message wins.
                    leafMessage = node.Message;
                }
            }
            return leafMessage != null;
        }

        /// <summary>
        /// Depth-first walk of an exception and everything nested within it (the
        /// <see cref="Exception.InnerException"/> chain and <see cref="AggregateException.InnerExceptions"/>),
        /// outermost first.
        /// </summary>
        public static IEnumerable<Exception> Flatten(Exception ex)
        {
            if (ex == null) yield break;
            yield return ex;

            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    foreach (var n in Flatten(inner)) yield return n;
                }
            }
            else if (ex.InnerException != null)
            {
                foreach (var n in Flatten(ex.InnerException)) yield return n;
            }
        }
    }
}

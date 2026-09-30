using System;
using System.Net;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Decides whether a failed Microsoft Graph delta request means the delta token it carried is dead, so the
    /// caller must throw the token away and start again with a full read. Every other failure must KEEP the
    /// token (issue #664).
    /// </summary>
    /// <remarks>
    /// Graph documents two ways a delta token stops working, and production has shown a third:
    /// <list type="bullet">
    /// <item><b>Synchronisation reset</b> - <c>410 Gone</c>: "the application must restart with a full
    /// synchronization".</item>
    /// <item><b>Documented expiry</b> - "a 40X-series error with error codes such as <c>syncStateNotFound</c>".</item>
    /// <item><b>Expiry as actually observed on <c>/users/delta</c></b> - <c>400</c> with error code
    /// <c>Request_UnsupportedQuery</c> ("DeltaLink older than 30 days is not supported."). It matches neither
    /// documented shape, and the age it quotes is not the documented seven days, so no fix can rely on the
    /// documented code or on any time limit alone.</item>
    /// </list>
    /// The decision reads only the HTTP status and Graph's machine-readable <c>error.code</c>, never the
    /// message text, which Graph says not to depend on.
    ///
    /// <para>
    /// Only a request that <b>carried</b> a token can reject one. <c>Request_UnsupportedQuery</c> on a request
    /// without a token means the query itself is wrong, and discarding a token would not fix it. Throttling
    /// (429), authorisation failures (401/403), server errors (5xx), timeouts and transport failures never
    /// qualify either: they say nothing about the token, and discarding it would turn a transient fault into a
    /// full re-read of the tenant. Keeping the checkpoint through those is the rule set by #372, #493 and #494.
    /// </para>
    /// </remarks>
    public static class GraphDeltaTokenRejection
    {
        /// <summary>Graph's documented error code for an expired delta token.</summary>
        public const string SyncStateNotFoundErrorCode = "syncStateNotFound";

        /// <summary>The error code <c>/users/delta</c> returns, with a 400, for a token it considers too old.</summary>
        public const string UnsupportedQueryErrorCode = "Request_UnsupportedQuery";

        /// <summary>
        /// True when <paramref name="failure"/> says the delta token sent with the request has expired or been
        /// reset, so it must be discarded and the data read again in full.
        /// </summary>
        /// <param name="failure">What the failed request threw.</param>
        /// <param name="requestCarriedDeltaToken">
        /// Whether the failed request was the one that carried the stored <c>$deltatoken</c>. Paging links
        /// (<c>$skiptoken</c>) and first-run reads do not, and their failures are never a token rejection.
        /// </param>
        public static bool IsRejectedDeltaToken(Exception failure, bool requestCarriedDeltaToken)
        {
            if (!requestCarriedDeltaToken)
            {
                return false;
            }

            // A plain HttpRequestException is what AutoThrottleHttpClient throws once its 429 / 502-504 retries
            // run out, and what a transport failure looks like. Neither carries a Graph error payload, and neither
            // says anything about the token.
            var graphError = failure as GraphHttpException;
            if (graphError == null)
            {
                return false;
            }

            var status = (int)graphError.StatusCode;
            if (status < 400 || status >= 500
                || graphError.StatusCode == HttpStatusCode.Unauthorized
                || graphError.StatusCode == HttpStatusCode.Forbidden
                || status == 429)
            {
                return false;
            }

            if (graphError.StatusCode == HttpStatusCode.Gone)
            {
                return true;
            }

            if (string.Equals(graphError.GraphErrorCode, SyncStateNotFoundErrorCode, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return graphError.StatusCode == HttpStatusCode.BadRequest
                && string.Equals(graphError.GraphErrorCode, UnsupportedQueryErrorCode, StringComparison.OrdinalIgnoreCase);
        }
    }
}

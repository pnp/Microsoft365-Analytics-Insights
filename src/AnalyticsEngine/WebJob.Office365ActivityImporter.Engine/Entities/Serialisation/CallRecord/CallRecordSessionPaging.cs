using System;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// A call record's sessions could not all be read. The call must then be neither saved nor reported as processed:
    /// a truncated call loses attendees, and under <c>UserGroupsFilter</c> it can be skipped outright because the only
    /// in-scope people on it were on a page that was never read.
    /// </summary>
    public class CallRecordSessionPagingException : Exception
    {
        public CallRecordSessionPagingException(string callId, string message, bool isPermanent, Exception innerException = null)
            : base(message, innerException)
        {
            CallId = callId;
            IsPermanent = isPermanent;
        }

        public string CallId { get; }

        /// <summary>
        /// True when retrying cannot help, because the same call would page the same way again (too many pages, a
        /// continuation link that repeats or points somewhere it must not). False for a page that failed to load.
        /// </summary>
        public bool IsPermanent { get; }
    }

    /// <summary>
    /// Rules for following a call record's session continuation links.
    /// </summary>
    public static class CallRecordSessionPaging
    {
        /// <summary>
        /// Graph's maximum page size for sessions, expanded inline or listed.
        /// </summary>
        public const int GraphSessionsPageSize = 60;

        /// <summary>
        /// The most pages of sessions read for one call, the first included: 30,000 sessions, far beyond any real Teams
        /// meeting (1,000 interactive attendees). Reaching it means the paging has run away, so the load fails rather
        /// than saving a truncated call.
        /// </summary>
        public const int MaxPages = 500;

        private const string MetadataFragmentMarker = "/$metadata#";

        /// <summary>
        /// The URL to request for a sessions continuation link.
        /// </summary>
        /// <param name="nextLink">The <c>sessions@odata.nextLink</c> or <c>@odata.nextLink</c> value Graph returned.</param>
        /// <param name="callRecordUrl">The absolute URL the call record itself was read from.</param>
        /// <remarks>
        /// Graph's own documentation shows this link as <c>.../v1.0/$metadata#communications/callRecords('{id}')/sessions?...</c>.
        /// Everything after <c>#</c> is a URL fragment, which is never sent, so requesting that link as it stands would
        /// download the service's metadata document instead of the next page. A resource URL never contains
        /// <c>$metadata#</c>, so it is taken out; any other link is followed exactly as Graph returned it.
        /// The link must stay on the call record's own host: the request carries the importer's Graph token.
        /// </remarks>
        public static string ResolveContinuationUrl(string nextLink, string callRecordUrl, string callId)
        {
            if (string.IsNullOrWhiteSpace(nextLink)) throw new ArgumentException("No continuation link.", nameof(nextLink));
            var callRecordUri = new Uri(callRecordUrl, UriKind.Absolute);

            var link = nextLink.Trim();
            var marker = link.IndexOf(MetadataFragmentMarker, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                link = link.Substring(0, marker) + "/" + link.Substring(marker + MetadataFragmentMarker.Length);
            }

            string url;
            if (Uri.TryCreate(link, UriKind.Absolute, out var absolute))
            {
                url = link;
            }
            else if (Uri.TryCreate(callRecordUri, link, out absolute))
            {
                url = absolute.AbsoluteUri;
            }
            else
            {
                throw new CallRecordSessionPagingException(callId, $"Call record '{callId}': Graph returned a sessions continuation link that is not a URL.", isPermanent: true);
            }

            if (!string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(absolute.Host, callRecordUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new CallRecordSessionPagingException(callId,
                    $"Call record '{callId}': refusing to follow a sessions continuation link to '{absolute.Scheme}://{absolute.Host}', which is not '{callRecordUri.Host}'.",
                    isPermanent: true);
            }

            return url;
        }
    }
}

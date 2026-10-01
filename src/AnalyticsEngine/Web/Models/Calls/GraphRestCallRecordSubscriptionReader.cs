using Azure.Core;
using Common.Entities.Calls;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.Calls
{
    /// <summary>
    /// Read-only <see cref="ICallRecordSubscriptionReader"/> over the Microsoft Graph REST API, used by
    /// the web app to show whether the Teams call-records webhook subscription is in place.
    /// </summary>
    /// <remarks>
    /// The web app makes exactly one kind of Graph request - list the subscriptions - so it calls
    /// <c>GET /v1.0/subscriptions</c> directly rather than referencing the Microsoft Graph SDK, whose main
    /// assembly alone added 41 MB (9 MB compressed) to the web site package. The importer, which creates
    /// and renews the subscription, still uses the SDK.
    /// </remarks>
    public class GraphRestCallRecordSubscriptionReader : ICallRecordSubscriptionReader
    {
        public const string SubscriptionsUrl = GraphRoot + "v1.0/subscriptions";

        private const string GraphRoot = "https://graph.microsoft.com/";

        private static readonly string[] GraphScopes = { GraphRoot + ".default" };

        /// <summary>
        /// Short, because an admin is waiting on the Service Configuration page while this runs.
        /// </summary>
        private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly JsonSerializerSettings GraphJsonSettings = new JsonSerializerSettings
        {
            // Keep expirationDateTime's offset exactly as Graph wrote it.
            DateParseHandling = DateParseHandling.DateTimeOffset,
        };

        private readonly TokenCredential _credential;
        private readonly HttpClient _httpClient;

        public GraphRestCallRecordSubscriptionReader(TokenCredential credential) : this(credential, SharedHttpClient) { }

        public GraphRestCallRecordSubscriptionReader(TokenCredential credential, HttpClient httpClient)
        {
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<IReadOnlyList<CallRecordSubscription>> FindCallRecordSubscriptions(Uri notificationUrl)
        {
            if (notificationUrl is null) throw new ArgumentNullException(nameof(notificationUrl));

            var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), CancellationToken.None).ConfigureAwait(false);

            var matching = new List<CallRecordSubscription>();
            var pageUrl = SubscriptionsUrl;
            while (!string.IsNullOrEmpty(pageUrl))
            {
                var page = await GetPage(pageUrl, token.Token).ConfigureAwait(false);
                foreach (var sub in page.Value ?? new List<GraphSubscription>())
                {
                    if (CallSubscriptionRules.IsCallRecordsSubscriptionFor(sub.Resource, sub.NotificationUrl, notificationUrl))
                    {
                        matching.Add(new CallRecordSubscription
                        {
                            Id = sub.Id,
                            Resource = sub.Resource,
                            NotificationUrl = sub.NotificationUrl,
                            ExpirationDateTime = sub.ExpirationDateTime,
                        });
                    }
                }

                pageUrl = page.NextLink;
            }

            return matching;
        }

        private async Task<SubscriptionPage> GetPage(string url, string accessToken)
        {
            // The token is only ever sent to Graph itself - the rule the Graph SDK's authentication
            // provider applies to every request, next-page links included.
            if (!url.StartsWith(GraphRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Not following a subscriptions page link outside {GraphRoot}: '{url}'.");
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using (var response = await _httpClient.SendAsync(request).ConfigureAwait(false))
                {
                    var body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new GraphRequestException(response.StatusCode, ReadGraphErrorMessage(body));
                    }

                    return JsonConvert.DeserializeObject<SubscriptionPage>(body, GraphJsonSettings) ?? new SubscriptionPage();
                }
            }
        }

        /// <summary>
        /// Graph's own error message, e.g. "Insufficient privileges to complete the operation." - the text
        /// the SDK's exception carried, and so what the status page has always shown. Null when the body
        /// is not a Graph error.
        /// </summary>
        private static string ReadGraphErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                var message = JsonConvert.DeserializeObject<GraphErrorResponse>(body)?.Error?.Message;
                return string.IsNullOrWhiteSpace(message) ? null : message;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private class SubscriptionPage
        {
            [JsonProperty("value")]
            public List<GraphSubscription> Value { get; set; }

            [JsonProperty("@odata.nextLink")]
            public string NextLink { get; set; }
        }

        private class GraphSubscription
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("resource")]
            public string Resource { get; set; }

            [JsonProperty("notificationUrl")]
            public string NotificationUrl { get; set; }

            [JsonProperty("expirationDateTime")]
            public DateTimeOffset? ExpirationDateTime { get; set; }
        }

        private class GraphErrorResponse
        {
            [JsonProperty("error")]
            public GraphError Error { get; set; }
        }

        private class GraphError
        {
            [JsonProperty("message")]
            public string Message { get; set; }
        }
    }

    /// <summary>
    /// A Graph REST request that Graph answered with a failure status. The message is Graph's own error
    /// text when it sent one, otherwise the HTTP status.
    /// </summary>
    public class GraphRequestException : Exception
    {
        public GraphRequestException(HttpStatusCode statusCode, string graphMessage)
            : base(string.IsNullOrWhiteSpace(graphMessage) ? $"{(int)statusCode} {statusCode}" : graphMessage)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}

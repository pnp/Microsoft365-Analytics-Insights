using App.ControlPanel.Engine.SPO.Auth;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.Rest
{
    /// <summary>
    /// Minimal SharePoint Online REST client, authenticated with the same MSAL bearer token the rest of the
    /// installer uses.
    ///
    /// This exists so the installer doesn't need Microsoft.SharePointOnline.CSOM. Everything the AITracker
    /// install does has a REST equivalent, and Microsoft Graph is not an option: it has no API for
    /// UserCustomActions, which is the mechanism the tracker is stapled to sites with.
    ///
    /// Notes on SharePoint's REST dialect:
    /// * OAuth bearer requests don't need an X-RequestDigest.
    /// * Deletes are POSTs with an "X-HTTP-Method: DELETE" header and "IF-MATCH: *".
    /// * odata=nometadata keeps responses flat; SharePoint infers the entity type from the endpoint.
    /// </summary>
    public class SpoRestClient : IDisposable
    {
        internal const string ACCEPT_JSON = "application/json;odata=nometadata";

        readonly ISpoAuthenticator _authenticator;
        readonly ILogger _logger;
        readonly HttpClient _httpClient;
        readonly bool _ownsHttpClient;

        public SpoRestClient(ISpoAuthenticator authenticator, ILogger logger)
            : this(authenticator, logger, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, true) { }

        public SpoRestClient(ISpoAuthenticator authenticator, ILogger logger, HttpClient httpClient)
            : this(authenticator, logger, httpClient, false) { }

        SpoRestClient(ISpoAuthenticator authenticator, ILogger logger, HttpClient httpClient, bool ownsHttpClient)
        {
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = ownsHttpClient;
        }

        /// <summary>
        /// A GET returning the parsed response, or null when SharePoint says the resource doesn't exist.
        /// </summary>
        public async Task<JObject> GetOrNullAsync(string url)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var response = await SendRawAsync(request, url);
                if (response.Status == HttpStatusCode.NotFound || IsNotFoundPayload(response))
                {
                    return null;
                }
                EnsureSuccess(response, url);
                return Parse(response.Body, url);
            }
        }

        public async Task<JObject> GetAsync(string url)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var response = await SendRawAsync(request, url);
                EnsureSuccess(response, url);
                return Parse(response.Body, url);
            }
        }

        /// <summary>The "value" array of a collection GET.</summary>
        public async Task<JArray> GetCollectionAsync(string url)
        {
            var body = await GetAsync(url);
            return body["value"] as JArray ?? new JArray();
        }

        public async Task<JObject> PostAsync(string url, object body = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                if (body != null)
                {
                    var json = body is string s ? s : JObject.FromObject(body).ToString();
                    request.Content = new StringContent(json, Encoding.UTF8);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ACCEPT_JSON + ";charset=utf-8");
                }
                var response = await SendRawAsync(request, url);
                EnsureSuccess(response, url);
                return string.IsNullOrWhiteSpace(response.Body) ? new JObject() : Parse(response.Body, url);
            }
        }

        /// <summary>Uploads raw bytes, for the file-add endpoints.</summary>
        public async Task<JObject> PostBytesAsync(string url, byte[] content)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new ByteArrayContent(content);
                var response = await SendRawAsync(request, url);
                EnsureSuccess(response, url);
                return string.IsNullOrWhiteSpace(response.Body) ? new JObject() : Parse(response.Body, url);
            }
        }

        /// <summary>SharePoint deletes are POSTs carrying X-HTTP-Method: DELETE.</summary>
        public async Task DeleteAsync(string url)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Add("X-HTTP-Method", "DELETE");
                request.Headers.TryAddWithoutValidation("IF-MATCH", "*");
                var response = await SendRawAsync(request, url);
                EnsureSuccess(response, url);
            }
        }

        internal class RawResponse
        {
            public HttpStatusCode Status { get; set; }
            public string Reason { get; set; }
            public string Body { get; set; }
            public bool IsSuccess { get; set; }
        }

        async Task<RawResponse> SendRawAsync(HttpRequestMessage request, string url)
        {
            request.Headers.Add("accept", ACCEPT_JSON);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _authenticator.GetAccessTokenAsync(url));

            try
            {
                var response = await _httpClient.SendAsync(request);
                return new RawResponse
                {
                    Status = response.StatusCode,
                    Reason = response.ReasonPhrase,
                    Body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync(),
                    IsSuccess = response.IsSuccessStatusCode
                };
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
            {
                throw new SpoRestException($"Couldn't reach SharePoint at '{url}'. {ex.Message}", ex);
            }
        }

        void EnsureSuccess(RawResponse response, string url)
        {
            if (response.IsSuccess) return;

            var hint = string.Empty;
            if (response.Status == HttpStatusCode.Unauthorized || response.Status == HttpStatusCode.Forbidden)
            {
                hint = " The signed-in account needs to be a site collection administrator on this site.";
            }
            throw new SpoRestException(
                $"SharePoint returned {(int)response.Status} {response.Reason} for '{url}'.{hint} {Summarise(response.Body)}",
                response.Status);
        }

        /// <summary>
        /// SharePoint reports "list does not exist" as a 404 with an error payload rather than an empty body,
        /// so callers that treat absence as normal need to recognise it.
        /// </summary>
        static bool IsNotFoundPayload(RawResponse response)
        {
            if (response.IsSuccess || string.IsNullOrEmpty(response.Body)) return false;
            return response.Body.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                || response.Body.IndexOf("System.IO.FileNotFoundException", StringComparison.OrdinalIgnoreCase) >= 0
                || response.Body.IndexOf("-2147024809", StringComparison.Ordinal) >= 0;
        }

        static JObject Parse(string body, string url)
        {
            if (string.IsNullOrWhiteSpace(body)) return new JObject();
            try
            {
                return JObject.Parse(body);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new SpoRestException($"Unreadable response from '{url}': {Summarise(body)}", ex);
            }
        }

        static string Summarise(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;
            var trimmed = body.Trim();
            return trimmed.Length > 500 ? trimmed.Substring(0, 500) + "..." : trimmed;
        }

        /// <summary>Escapes a value for use inside a SharePoint OData string literal ('...').</summary>
        public static string ODataLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        public void Dispose()
        {
            if (_ownsHttpClient) _httpClient?.Dispose();
        }
    }

    /// <summary>A SharePoint REST call failed.</summary>
    public class SpoRestException : Exception
    {
        public SpoRestException(string message) : base(message) { }
        public SpoRestException(string message, Exception inner) : base(message, inner) { }
        public SpoRestException(string message, HttpStatusCode status) : base(message) { Status = status; }

        public HttpStatusCode? Status { get; }

        public bool IsAccessDenied => Status == HttpStatusCode.Unauthorized || Status == HttpStatusCode.Forbidden;
    }

    /// <summary>
    /// The bits of a SharePoint web the tracker installer needs. Replaces CSOM's
    /// Microsoft.SharePoint.Client.Web as the generic argument to ISiteInstallAdaptor.
    /// </summary>
    public class SpoWeb
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string ServerRelativeUrl { get; set; }

        public static SpoWeb FromJson(JToken json)
        {
            return new SpoWeb
            {
                Url = json["Url"]?.ToString(),
                Title = json["Title"]?.ToString(),
                ServerRelativeUrl = json["ServerRelativeUrl"]?.ToString()
            };
        }

        public static List<SpoWeb> ListFromJson(JArray json)
        {
            var webs = new List<SpoWeb>();
            foreach (var item in json)
            {
                webs.Add(FromJson(item));
            }
            return webs;
        }
    }
}

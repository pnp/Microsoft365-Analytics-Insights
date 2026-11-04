using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Common.Entities.UserScope
{
    /// <summary>An Entra ID group, as far as scope resolution needs it.</summary>
    public class DirectoryGroup
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }
    }

    /// <summary>A user member of a group.</summary>
    public class DirectoryUser
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }
    }

    /// <summary>One page of a Graph collection.</summary>
    public class DirectoryPage<T>
    {
        [JsonProperty("value")]
        public List<T> Items { get; set; } = new List<T>();

        [JsonProperty("@odata.nextLink")]
        public string NextLink { get; set; }
    }

    /// <summary>
    /// The four directory reads scope resolution needs. A port so <see cref="GroupMembershipResolver"/> can be
    /// tested with no HTTP at all.
    /// </summary>
    /// <remarks>
    /// Every method throws when the directory cannot answer - permissions, throttling that outlasted the retries,
    /// an outage, or a success response that is not the page or group asked for. Only "no such group" is an answer
    /// rather than a failure, and the resolver depends on that distinction: a filter that names no real group means
    /// nobody is in scope, whereas a failed read means the scope is unknown.
    /// </remarks>
    public interface IGroupDirectoryReader
    {
        /// <summary>The group with this object id, or null when there is none.</summary>
        Task<DirectoryGroup> GetGroupByIdAsync(Guid groupId);

        /// <summary>Every group with exactly this display name. Empty when there are none.</summary>
        Task<IReadOnlyList<DirectoryGroup>> FindGroupsByDisplayNameAsync(string displayName);

        /// <summary>One page of all groups. Pass null for the first page, then each page's next link.</summary>
        Task<DirectoryPage<DirectoryGroup>> ListGroupsAsync(string nextLink);

        /// <summary>
        /// One page of a group's direct <b>user</b> members. Pass null for the first page, then each page's
        /// next link.
        /// </summary>
        Task<DirectoryPage<DirectoryUser>> ListUserMembersAsync(string groupId, string nextLink);
    }

    /// <summary>A directory read that did not succeed.</summary>
    public class DirectoryReadException : Exception
    {
        public DirectoryReadException(HttpStatusCode statusCode, string url, string responseBody)
            : base($"Graph returned HTTP {(int)statusCode} ({statusCode}) for {url}. {Describe(statusCode)}")
        {
            StatusCode = statusCode;
            Url = url;
            ResponseBody = responseBody;
        }

        public HttpStatusCode StatusCode { get; }
        public string Url { get; }
        public string ResponseBody { get; }

        private static string Describe(HttpStatusCode statusCode)
        {
            if (statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.Unauthorized)
            {
                return "The app registration needs the Group.Read.All (or GroupMember.Read.All) application permission, admin-consented.";
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// A directory read that Graph answered with a success status, but not with what was asked for: a blank or
    /// <c>null</c> body, a page without its <c>value</c> collection, or a group without its id.
    /// </summary>
    /// <remarks>
    /// Such a response is a failed read, never an empty one. The members it should have listed are unknown, and
    /// counting them as none would drop them from the scope - which makes them candidates for the User scope purge.
    /// A page with an explicit <c>"value": []</c> is a real, empty page.
    /// </remarks>
    public class IncompleteDirectoryResponseException : Exception
    {
        public IncompleteDirectoryResponseException(string endpoint, string problem, Exception innerException = null)
            : base($"Graph answered {endpoint} with a success status, but {problem}, so what it should have returned is unknown.", innerException)
        {
            Endpoint = endpoint;
        }

        /// <summary>The request's endpoint shape, without identifiers or paging tokens.</summary>
        public string Endpoint { get; }
    }

    /// <summary>Microsoft Graph implementation of <see cref="IGroupDirectoryReader"/>.</summary>
    public class GraphGroupDirectoryReader : IGroupDirectoryReader
    {
        /// <summary>Graph's maximum page size for directory objects.</summary>
        internal const int PageSize = 999;

        private const string GraphRoot = "https://graph.microsoft.com/v1.0";

        /// <summary>Strings stay strings: a group named like a timestamp must not come back reformatted as a date.</summary>
        private static readonly JsonSerializerSettings ReadSettings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

        private readonly AutoThrottleHttpClient _httpClient;
        private readonly ILogger _logger;

        public GraphGroupDirectoryReader(AutoThrottleHttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? NullLogger.Instance;
        }

        public async Task<DirectoryGroup> GetGroupByIdAsync(Guid groupId)
        {
            var url = $"{GraphRoot}/groups/{groupId}?$select=id,displayName";
            var body = await GetBodyAsync(url, notFoundIsNull: true);
            if (body == null)
            {
                return null;
            }

            // Only a 404 means "no such group". A success without the group's id is not an answer: read as one, it would
            // resolve the filter as if the group did not exist and leave its members out of the scope.
            var group = ToObject<DirectoryGroup>(ParseObject(body, url), url);
            if (string.IsNullOrEmpty(group?.Id))
            {
                throw new IncompleteDirectoryResponseException(Endpoint(url), "the group came back without its id");
            }
            return group;
        }

        public async Task<IReadOnlyList<DirectoryGroup>> FindGroupsByDisplayNameAsync(string displayName)
        {
            // OData string literals escape a single quote by doubling it. Without this a group named
            // "Bob's pilot" would produce a malformed filter and a 400.
            var literal = (displayName ?? string.Empty).Replace("'", "''");
            var url = $"{GraphRoot}/groups?$filter=displayName eq '{Uri.EscapeDataString(literal)}'&$select=id,displayName&$top={PageSize}";

            var page = await GetPageAsync<DirectoryGroup>(url);
            return page.Items.Where(g => !string.IsNullOrEmpty(g?.Id)).ToList();
        }

        public Task<DirectoryPage<DirectoryGroup>> ListGroupsAsync(string nextLink)
        {
            var url = nextLink ?? $"{GraphRoot}/groups?$select=id,displayName&$top={PageSize}";
            return GetPageAsync<DirectoryGroup>(url);
        }

        public Task<DirectoryPage<DirectoryUser>> ListUserMembersAsync(string groupId, string nextLink)
        {
            // Direct members only, cast to users: nested groups are not expanded (the product has always matched
            // direct membership), and devices and service principals are not people.
            var url = nextLink ?? $"{GraphRoot}/groups/{Uri.EscapeDataString(groupId)}/members/microsoft.graph.user" +
                                 $"?$select=id,userPrincipalName,mail,accountEnabled&$top={PageSize}";
            return GetPageAsync<DirectoryUser>(url);
        }

        private async Task<DirectoryPage<T>> GetPageAsync<T>(string url)
            => ParsePage<T>(await GetBodyAsync(url, notFoundIsNull: false), url);

        /// <summary>
        /// One page of a Graph collection. Throws <see cref="IncompleteDirectoryResponseException"/> unless the body is a
        /// JSON object whose <c>value</c> is an array: a blank body, <c>null</c>, <c>{}</c> or a page whose <c>value</c> is
        /// missing or not a collection says nothing about the members, so it cannot be taken for an empty or last page.
        /// </summary>
        private static DirectoryPage<T> ParsePage<T>(string body, string url)
        {
            var json = ParseObject(body, url);
            var value = json.GetValue("value", StringComparison.OrdinalIgnoreCase);
            if (value == null)
            {
                throw new IncompleteDirectoryResponseException(Endpoint(url), "the page has no 'value' collection");
            }
            if (value.Type != JTokenType.Array)
            {
                throw new IncompleteDirectoryResponseException(Endpoint(url), $"the page's 'value' is {Describe(value)}, not a collection");
            }

            return ToObject<DirectoryPage<T>>(json, url);
        }

        private static JObject ParseObject(string body, string url)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new IncompleteDirectoryResponseException(Endpoint(url), "the body was empty");
            }

            JToken token;
            try
            {
                token = JsonConvert.DeserializeObject<JToken>(body, ReadSettings);
            }
            catch (JsonException ex)
            {
                throw new IncompleteDirectoryResponseException(Endpoint(url), "the body is not valid JSON", ex);
            }

            return token as JObject
                ?? throw new IncompleteDirectoryResponseException(Endpoint(url), $"the body is {Describe(token)}, not a JSON object");
        }

        private static T ToObject<T>(JObject json, string url)
        {
            try
            {
                return json.ToObject<T>();
            }
            catch (JsonException ex)
            {
                throw new IncompleteDirectoryResponseException(Endpoint(url), $"the response could not be read ({ex.GetType().Name})", ex);
            }
        }

        private static string Describe(JToken token)
        {
            switch (token?.Type ?? JTokenType.Null)
            {
                case JTokenType.Null: return "null";
                case JTokenType.Array: return "an array";
                case JTokenType.Object: return "an object";
                case JTokenType.String: return "a string";
                default: return "a " + token.Type.ToString().ToLowerInvariant();
            }
        }

        /// <summary>The endpoint's shape only: a URL carries group ids and, on later pages, a skip token.</summary>
        private static string Endpoint(string url) => GraphEndpointTemplate.FromUrl("GET", url);

        /// <summary>The body of a success response, or null for a 404 when <paramref name="notFoundIsNull"/>.</summary>
        private async Task<string> GetBodyAsync(string url, bool notFoundIsNull)
        {
            using (var response = await _httpClient.GetAsyncWithThrottleRetries(url, _logger))
            {
                if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull)
                {
                    return null;
                }

                var body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new DirectoryReadException(response.StatusCode, url, body);
                }

                return body ?? string.Empty;
            }
        }
    }
}

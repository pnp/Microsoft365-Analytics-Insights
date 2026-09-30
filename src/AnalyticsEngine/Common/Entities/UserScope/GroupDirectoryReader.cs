using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
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
    /// an outage. Only "no such group" is an answer rather than a failure, and the resolver depends on that
    /// distinction: a filter that names no real group means nobody is in scope, whereas a failed read means the
    /// scope is unknown.
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

    /// <summary>Microsoft Graph implementation of <see cref="IGroupDirectoryReader"/>.</summary>
    public class GraphGroupDirectoryReader : IGroupDirectoryReader
    {
        /// <summary>Graph's maximum page size for directory objects.</summary>
        internal const int PageSize = 999;

        private const string GraphRoot = "https://graph.microsoft.com/v1.0";

        private readonly AutoThrottleHttpClient _httpClient;
        private readonly ILogger _logger;

        public GraphGroupDirectoryReader(AutoThrottleHttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? NullLogger.Instance;
        }

        public async Task<DirectoryGroup> GetGroupByIdAsync(Guid groupId)
        {
            var group = await GetJsonAsync<DirectoryGroup>($"{GraphRoot}/groups/{groupId}?$select=id,displayName", notFoundIsNull: true);
            return string.IsNullOrEmpty(group?.Id) ? null : group;
        }

        public async Task<IReadOnlyList<DirectoryGroup>> FindGroupsByDisplayNameAsync(string displayName)
        {
            // OData string literals escape a single quote by doubling it. Without this a group named
            // "Bob's pilot" would produce a malformed filter and a 400.
            var literal = (displayName ?? string.Empty).Replace("'", "''");
            var url = $"{GraphRoot}/groups?$filter=displayName eq '{Uri.EscapeDataString(literal)}'&$select=id,displayName&$top={PageSize}";

            var page = await GetJsonAsync<DirectoryPage<DirectoryGroup>>(url, notFoundIsNull: false);
            return page?.Items?.Where(g => !string.IsNullOrEmpty(g?.Id)).ToList() ?? new List<DirectoryGroup>();
        }

        public async Task<DirectoryPage<DirectoryGroup>> ListGroupsAsync(string nextLink)
        {
            var url = nextLink ?? $"{GraphRoot}/groups?$select=id,displayName&$top={PageSize}";
            return await GetJsonAsync<DirectoryPage<DirectoryGroup>>(url, notFoundIsNull: false) ?? new DirectoryPage<DirectoryGroup>();
        }

        public async Task<DirectoryPage<DirectoryUser>> ListUserMembersAsync(string groupId, string nextLink)
        {
            // Direct members only, cast to users: nested groups are not expanded (the product has always matched
            // direct membership), and devices and service principals are not people.
            var url = nextLink ?? $"{GraphRoot}/groups/{Uri.EscapeDataString(groupId)}/members/microsoft.graph.user" +
                                 $"?$select=id,userPrincipalName,mail,accountEnabled&$top={PageSize}";
            return await GetJsonAsync<DirectoryPage<DirectoryUser>>(url, notFoundIsNull: false) ?? new DirectoryPage<DirectoryUser>();
        }

        private async Task<T> GetJsonAsync<T>(string url, bool notFoundIsNull) where T : class
        {
            using (var response = await _httpClient.GetAsyncWithThrottleRetries(url, _logger))
            {
                if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new DirectoryReadException(response.StatusCode, url, body);
                }

                return JsonConvert.DeserializeObject<T>(body);
            }
        }
    }
}

using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.User
{
    /// <summary>
    /// Loads Entra ID group memberships for users from Microsoft Graph.
    /// </summary>
    public class GraphUserGroupsCache : UserGroupsCache
    {
        private readonly ManualGraphCallClient _httpClient;

        public GraphUserGroupsCache(ManualGraphCallClient httpClient, ILogger logger)
            : base(logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        protected override async Task<List<string>> LoadGroupsFromGraphAsync(string upn)
        {
            var result = new List<string>();
            try
            {
                string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(upn)}/memberOf?$select=displayName";
                var response = await _httpClient.GetAsyncWithThrottleRetries<JObject>(url);
                if (response != null && response["value"] is JArray arr)
                {
                    foreach (var group in arr)
                    {
                        var displayName = group["displayName"]?.ToString();
                        if (!string.IsNullOrEmpty(displayName))
                            result.Add(displayName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to load groups for user '{upn}'");
            }
            return result;
        }
    }
}

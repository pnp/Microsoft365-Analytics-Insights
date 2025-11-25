using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Graph API implementation of user metadata loader
    /// </summary>
    public class GraphUserLoader : IUserMetadataLoader
    {
        private readonly ManualGraphCallClient _httpClient;
        private readonly IDeltaValueProvider _deltaValueProvider;
        private readonly ILogger _telemetry;
        private readonly GraphServiceClient _graphServiceClient;

        public GraphUserLoader(ManualGraphCallClient httpClient, IDeltaValueProvider deltaValueProvider, ILogger telemetry, GraphServiceClient graphServiceClient)
        {
            this._httpClient = httpClient;
            _deltaValueProvider = deltaValueProvider;
            this._telemetry = telemetry;
            this._graphServiceClient = graphServiceClient;
        }

        public IDeltaValueProvider DeltaValueProvider => _deltaValueProvider;

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Cache delta using tenant ID
            var usersQueryDelta = await _deltaValueProvider.GetDeltaToken();
            var initialDeltaUrl = $"https://graph.microsoft.com:443/v1.0/users/delta" +
                "?$select=id,accountEnabled,officeLocation,usageLocation,jobTitle,department,mail,userPrincipalName,manager,companyName,postalCode,country,state" +
                "&$expand=manager";
            if (!string.IsNullOrEmpty(usersQueryDelta))
            {
                initialDeltaUrl += $"&$deltatoken={usersQueryDelta}";
            }

            var results = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphUser>(initialDeltaUrl, _telemetry,
                async (deltaLink) =>
                {
                    var thisPageDelta = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    await _deltaValueProvider.SetDeltaToken(thisPageDelta);
                });


            if (string.IsNullOrEmpty(usersQueryDelta))
            {
                _telemetry.LogInformation($"User import - read {results.Count.ToString("N0")} users (all) from Graph API");
            }
            else
            {
                _telemetry.LogInformation($"User import - read {results.Count.ToString("N0")} updated users from Graph API, using last delta.");
            }

            // Graph for some reason gives duplicates; filter that out
            var allGraphUsers = results.GroupBy(u => u.UserPrincipalName).Select(g => g.First()).ToList();
            var allActiveGraphUsers = allGraphUsers.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value).ToList();

            return allActiveGraphUsers;
        }

        public async Task<IGraphServiceSubscribedSkusCollectionPage> LoadTenantSkus()
        {
            try
            {
                return await _graphServiceClient.SubscribedSkus.Request().GetAsync();
            }
            catch (ServiceException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _telemetry.LogError($"User import - couldn't load SKUs for org - {ex.Message}. Ensure 'Organization.Read.All' in granted.");
                }
                else
                {
                    _telemetry.LogError(ex, $"User import - couldn't load SKUs for org - {ex.Message}");
                }

                // If we can't get tenant SKUs to find all users by, we can get SKUs per user instead, but this can be very slow.
                _telemetry.LogWarning($"User import - will load SKUs directly from each user instead. This will be slow.");
                return null;
            }
        }

        public async Task<List<Microsoft.Graph.User>> LoadUsersBySku(Guid skuId)
        {
            var req = _graphServiceClient.Users.Request()
                .Select("userPrincipalName")
                .Filter($"assignedLicenses/any(u:u/skuId eq {skuId})");

            // Recursively load users 
            var allUsersWithSku = new List<Microsoft.Graph.User>();
            int skuPage = 1;
            while (req != null)
            {
                var usersWithSku = await req.GetAsync();
                allUsersWithSku.AddRange(usersWithSku);
                req = usersWithSku.NextPageRequest;
                Console.WriteLine($"DEBUG: SKU {skuId} page {skuPage}");
                skuPage++;
            }

            return allUsersWithSku;
        }

        public async Task<IUserLicenseDetailsCollectionPage> LoadUserLicenseDetails(string userId)
        {
            try
            {
                return await _graphServiceClient.Users[userId].LicenseDetails.Request()
                    .Select("skuPartNumber,skuId")
                    .GetAsync();
            }
            catch (ServiceException ex)
            {
                _telemetry.LogError(ex, $"User import - couldn't load service-plans for user ID '{userId}' - {ex.Message}");
                return null;
            }
        }
    }
}

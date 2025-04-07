using DataUtils;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public class GraphUserLoader
    {
        private readonly ManualGraphCallClient _httpClient;
        private readonly IDeltaValueProvider _deltaValueProvider;
        private readonly ILogger _telemetry;

        public GraphUserLoader(ManualGraphCallClient httpClient, IDeltaValueProvider deltaValueProvider, ILogger telemetry)
        {
            this._httpClient = httpClient;
            _deltaValueProvider = deltaValueProvider;
            this._telemetry = telemetry;
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

    }
}

using Common.Entities.Models;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web.Http;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class SiteTokenAPIController : BaseAPIController
    {
        // POST: api/SiteTokenAPI
        // For returning to teams-permission-grant JS app the server-side generated OAuth token for user
        public async Task<JSonToken> Post()
        {
            var auth = await base.GetCachedUserAccessTokenAsync();

            // Test graph call
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            var response = await httpClient.GetAsync("https://graph.microsoft.com/v1.0/me/joinedTeams");
            response.EnsureSuccessStatusCode();


            return new JSonToken(auth);
        }
    }
}

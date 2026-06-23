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
        // A single HttpClient reused for the app's lifetime. Creating one per request (the previous
        // behaviour) leaks sockets and can exhaust ephemeral ports under load.
        private static readonly HttpClient _httpClient = new HttpClient();

        // POST: api/SiteTokenAPI
        // For returning to the admin-app JS app the server-side generated OAuth token for user
        public async Task<JSonToken> Post()
        {
            var auth = await base.GetCachedUserAccessTokenAsync();
            if (auth == null || string.IsNullOrEmpty(auth.AccessToken))
            {
                throw new HttpResponseException(System.Net.HttpStatusCode.Unauthorized);
            }

            // Test graph call. Use a per-request message so the shared client's default headers
            // aren't mutated concurrently across requests.
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/joinedTeams"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }

            return new JSonToken(auth);
        }
    }
}

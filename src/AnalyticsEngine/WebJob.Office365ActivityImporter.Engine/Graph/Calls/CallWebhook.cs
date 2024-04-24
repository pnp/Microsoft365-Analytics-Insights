using Azure.Identity;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Used to ensure call webhooks are in place & valid
    /// </summary>
    public class CallWebhook
    {
        public GraphServiceClient Client { get; set; }
        public ILogger Telemetry { get; set; }

        public CallWebhook(AppConfig o365DownloadSettings, ILogger telemetry)
            : this(o365DownloadSettings?.TenantGUID.ToString(), o365DownloadSettings?.ClientID, o365DownloadSettings?.ClientSecret, telemetry) { }

        public CallWebhook(string tenantId, string clientId, string secret, ILogger telemetry)
        {
            var cred = new ClientSecretCredential(tenantId, clientId, secret);

            this.Telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            this.Client = new GraphServiceClient(cred);
        }

        public async Task CreateOrUpdateWebhook(Uri webAppUrl, string secret)
        {
            var allSubs = await this.Client.Subscriptions.Request().GetAsync();

            // https://docs.microsoft.com/en-us/graph/api/resources/webhooks?view=graph-rest-1.0
            const string CALL_TYPE = "/communications/callRecords";
            var subs = allSubs.Where(s => s.Resource == CALL_TYPE && s.NotificationUrl == webAppUrl.ToString());
            if (subs.Count() == 0)
            {
                Telemetry.LogInformation($"No subscription found for call-records, for URL '{webAppUrl}'. Creating...");
                try
                {
                    var result = await this.Client.Subscriptions.Request().AddAsync(new Subscription()
                    {
                        NotificationUrl = webAppUrl.ToString(),
                        Resource = CALL_TYPE,
                        ClientState = secret,
                        ChangeType = "created",
                        ExpirationDateTime = DateTime.Now.AddDays(2)        // the max Graph will permit - https://docs.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-beta#properties
                    });
                    Telemetry.LogInformation($"Created subscription id '{result.Id}' for webhook.");
                }
                catch (ServiceException ex)
                {
                    Telemetry.LogError(ex, $"Couldn't create webhook at URL '{webAppUrl}'. Got exception: '{ex.Message}'");
                }

            }
            else
            {
                // https://docs.microsoft.com/en-us/graph/api/subscription-update?view=graph-rest-beta&tabs=http

                var result = this.Client.Subscriptions[subs.First().Id].Request().UpdateAsync(
                    new Subscription
                    {
                        ExpirationDateTime = DateTime.Now.AddDays(2)
                    }
                );
                Telemetry.LogInformation($"Updated subscription '{result.Id}' for webhook, for URL '{webAppUrl}'.");
            }
        }
    }
}

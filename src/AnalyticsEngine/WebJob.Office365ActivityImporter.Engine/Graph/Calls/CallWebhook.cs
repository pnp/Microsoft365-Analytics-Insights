using Azure.Identity;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
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
            // https://docs.microsoft.com/en-us/graph/api/resources/webhooks?view=graph-rest-1.0
            const string CALL_TYPE = "/communications/callRecords";

            // Walk all pages of subscriptions. The default page size (~100) is smaller than the
            // per-app subscription cap; if the tenant has more than one page we'd otherwise miss
            // an existing matching subscription and create a duplicate.
            var matchingSubs = new List<Subscription>();
            var subsPage = await this.Client.Subscriptions.Request().GetAsync();
            while (subsPage != null)
            {
                matchingSubs.AddRange(subsPage.Where(s => s.Resource == CALL_TYPE && s.NotificationUrl == webAppUrl.ToString()));
                if (subsPage.NextPageRequest == null) break;
                subsPage = await subsPage.NextPageRequest.GetAsync();
            }

            if (!matchingSubs.Any())
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
                        ExpirationDateTime = DateTime.UtcNow.AddDays(2)        // the max Graph will permit - https://docs.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-beta#properties
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
                // Must await so the renewal actually happens and so result.Id is the Subscription id (not the Task id).
                try
                {
                    var existing = matchingSubs.First();
                    var result = await this.Client.Subscriptions[existing.Id].Request().UpdateAsync(
                        new Subscription
                        {
                            ExpirationDateTime = DateTime.UtcNow.AddDays(2)
                        }
                    );
                    Telemetry.LogInformation($"Updated subscription '{result.Id}' for webhook, for URL '{webAppUrl}'.");
                }
                catch (ServiceException ex)
                {
                    Telemetry.LogError(ex, $"Couldn't update webhook at URL '{webAppUrl}'. Got exception: '{ex.Message}'");
                }
            }
        }
    }
}

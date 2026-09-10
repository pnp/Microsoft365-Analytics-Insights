using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    public class ActivitySubscriptionManager : AbstractActivityApiLoaderWithHttpClient, IActivitySubscriptionManager
    {
        public ActivitySubscriptionManager(AppConfig settings, AnalyticsLogger logger, ConfidentialClientApplicationThrottledHttpClient httpClient)
            : base(logger, httpClient, settings)
        {
        }

        /// <summary>
        /// Create any subscription that's configured but not active.
        /// </summary>
        /// <remarks>
        /// A failure to create an OPTIONAL content type's subscription (see
        /// <see cref="ImportTaskSettings.IsOptionalContentType"/>) is logged and swallowed, so the rest of
        /// the audit import still runs. That matters because DLP.All needs its own
        /// <c>ActivityFeed.ReadDlp</c> permission: without this, an admin who ticks the DLP import before
        /// consenting to that grant would take SharePoint, Copilot and Power Platform importing down with
        /// it. Mandatory content types still throw - if Audit.SharePoint or Audit.General cannot be
        /// subscribed, the import cannot do its job and must fail loudly rather than silently import
        /// nothing.
        /// </remarks>
        public async Task CreateInactiveSubcriptions(List<string> active)
        {
            // Try and create it
            foreach (var configuredContentType in _settings.ContentTypesToRead)
            {
                if (!active.Contains(configuredContentType))
                {
                    try
                    {
                        Console.WriteLine("Creating subscription for content-type {0}. Need to wait a few seconds before it can be accessed...", configuredContentType);
                        var url = $"https://manage.office.com/api/v1.0/{_settings.TenantGUID}/activity/feed/subscriptions/start?ContentType={configuredContentType}";

                        Console.WriteLine("+{0}", url);
                        var response = await _httpClient.PostAsync(url, null);

                        var responseBody = string.Empty;
                        if (response.Content != null)
                        {
                            responseBody = await response.Content.ReadAsStringAsync();
                        }
                        try
                        {
                            response.EnsureSuccessStatusCode();
                        }
                        catch (HttpRequestException)
                        {
                            if (LogOptionalContentTypeFailure(configuredContentType, responseBody))
                            {
                                continue;
                            }

                            _logger.LogError("Can't create subscription. Check service-account permissions to Office 365 Activity API & that audit-log is turned on for tenant. https://docs.microsoft.com/en-gb/microsoft-365/compliance/turn-audit-log-search-on-or-off?view=o365-worldwide");
                            throw;
                        }

                        // Need to wait a few seconds before a new one can be accessed
                        await Task.Delay(5000);
                        Console.WriteLine($"Subscription for '{configuredContentType}' has been created.");
                    }
                    catch (HttpRequestException ex)
                    {
                        if (LogOptionalContentTypeFailure(configuredContentType, ex.Message))
                        {
                            continue;
                        }

                        // If we can't create it report the error
                        _logger.LogInformation($"Subscription for '{configuredContentType}' could not be found or created - {ex.Message}. Check the configuration file & app permissions in Azure AD.");
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Reports a subscription failure for an optional content type and returns true when the caller
        /// should carry on with the remaining content types. Returns false for mandatory content types,
        /// leaving the caller to rethrow.
        /// </summary>
        private bool LogOptionalContentTypeFailure(string contentType, string detail)
        {
            if (!ImportTaskSettings.IsOptionalContentType(contentType))
            {
                return false;
            }

            _logger.LogWarning(
                $"Could not subscribe to the optional Office 365 Activity API content-type '{contentType}' - {detail}. " +
                "This is usually a missing 'ActivityFeed.ReadDlp' ('Read DLP policy events') application permission on the " +
                "runtime app registration, which is a SEPARATE grant from 'ActivityFeed.Read' and needs its own admin " +
                "consent. Continuing without it: every other enabled workload still imports normally, and DLP data will " +
                "start arriving on the next cycle once the permission is granted. Note that Microsoft 365 Copilot DLP " +
                "reporting does NOT depend on this feed - it is carried on the Copilot interaction records themselves.");

            return true;
        }

        public async Task<List<string>> EnsureActiveSubscriptionContentTypesActive()
        {
            var active = await this.GetActiveSubscriptionContentTypes();
            var haveAllSubscriptionsActive = active.Count == _settings.ContentTypesToRead.Count;


            // If we don't have any subscriptions, create them
            if (!haveAllSubscriptionsActive)
            {
                await this.CreateInactiveSubcriptions(active);
                active = await this.GetActiveSubscriptionContentTypes();
            }

            return active;
        }

        public async Task<List<string>> GetActiveSubscriptionContentTypes()
        {
            List<string> validContentTypes = new List<string>();
            var allSubs = await this.GetActiveSubscriptions();

            foreach (var contentType in _settings.ContentTypesToRead)
            {
                // Try and find content type in all subs
                var sub = allSubs.FirstOrDefault(c => c.contentType == contentType);

                if (sub != null)
                {
                    if (sub.status.ToLower() != "enabled")
                    {
                        _logger.LogInformation(string.Format("Subscription for '{0}' is already in place, but not enabled.", contentType));
                    }
                    else
                    {
                        _logger.LogInformation(string.Format("Subscription for '{0}' is already in place and enabled.", contentType));
                        validContentTypes.Add(contentType);
                    }
                }
            }

            return validContentTypes;
        }

        /// <summary>
        /// Fetch the list of subscriptions
        /// </summary>
        public async Task<ApiSubscription[]> GetActiveSubscriptions()
        {
            return await GetActiveSubscriptions(_settings.TenantGUID.ToString(), _logger, _httpClient);
        }

        // To allow installer to call this method without needing an AppConfig
        public static async Task<ApiSubscription[]> GetActiveSubscriptions(string tenantId, ILogger logger, ConfidentialClientApplicationThrottledHttpClient _httpClient)
        {
            var url = $"https://manage.office.com/api/v1.0/{tenantId}/activity/feed/subscriptions/list";
            var response = await _httpClient.GetAsync(url);
            logger.LogInformation("Reading existing Office 365 Activity API subscriptions...");

            var responseBody = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            return JsonConvert.DeserializeObject<ApiSubscription[]>(responseBody);
        }
    }
}

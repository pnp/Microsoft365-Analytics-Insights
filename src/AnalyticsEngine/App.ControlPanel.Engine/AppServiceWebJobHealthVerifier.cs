using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    internal static class AppServiceWebJobHealthVerifier
    {
        internal const string ActivityImporterName = "Office365ActivityImporter";
        internal const string AppInsightsImporterName = "AppInsightsImporter";

        private static readonly string[] ExpectedWebJobs =
        {
            ActivityImporterName,
            AppInsightsImporterName,
        };

        internal static async Task VerifyAndLogAsync(
            WebSiteResource webApp,
            InstallerProxyConfig proxyConfig,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            try
            {
                var publishingProfile = webApp.GetPublishingProfileXmlWithSecrets(
                    new CsmPublishingProfile { Format = PublishingProfileFormat.WebDeploy });
                KuduPublishInfo publishInfo;
                using (var reader = new StreamReader(publishingProfile.Value))
                {
                    publishInfo = publishData.FromXml(reader).GetKuduPublishInfo();
                }

                var statuses = await GetStatusesAsync(publishInfo, proxyConfig, cancellationToken);
                var failures = FindFailures(statuses);

                if (failures.Count > 0)
                {
                    logger.LogError(
                        $"WebJob health check failed after App Service warm-up: {string.Join("; ", failures)}. " +
                        "Both continuous WebJobs must report 'Running' in the App Service WebJobs blade.");
                    return;
                }

                logger.LogInformation(
                    $"WebJob health check passed: {ActivityImporterName}=Running; {AppInsightsImporterName}=Running.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Could not verify continuous WebJob health after App Service warm-up: " +
                    CloudInstallEngine.ExceptionMessages.Format(ex));
            }
        }

        internal static List<string> FindFailures(IReadOnlyCollection<KuduContinuousWebJobStatus> statuses)
        {
            return ExpectedWebJobs
                    .Select(name =>
                    {
                        var job = statuses.FirstOrDefault(
                            status => string.Equals(status.Name, name, StringComparison.OrdinalIgnoreCase));
                        return job == null
                            ? $"{name}=missing"
                            : string.Equals(job.Status, "Running", StringComparison.OrdinalIgnoreCase)
                                ? null
                                : $"{name}={job.Status ?? "unknown"}";
                    })
                    .Where(failure => failure != null)
                    .ToList();
        }

        internal static async Task<List<KuduContinuousWebJobStatus>> GetStatusesAsync(
            KuduPublishInfo publishInfo,
            InstallerProxyConfig proxyConfig,
            CancellationToken cancellationToken)
        {
            using (var handler = InstallAppServiceContentsTask.CreateHttpClientHandler(proxyConfig))
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{publishInfo.Username}:{publishInfo.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

                var response = await client.GetAsync(
                    BuildKuduContinuousWebJobsUri(publishInfo.RootUrl),
                    cancellationToken);
                using (response)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"Kudu WebJob status request failed with HTTP {(int)response.StatusCode} " +
                            $"({response.ReasonPhrase}).");
                    }

                    return ParseStatuses(responseBody);
                }
            }
        }

        internal static Uri BuildKuduContinuousWebJobsUri(string publishUrl)
        {
            if (string.IsNullOrWhiteSpace(publishUrl))
                throw new ArgumentOutOfRangeException(nameof(publishUrl));

            var absoluteUrl = publishUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? publishUrl
                : "https://" + publishUrl;
            var profileUri = new Uri(absoluteUrl);
            return new Uri(profileUri.GetLeftPart(UriPartial.Authority) + "/api/continuouswebjobs");
        }

        internal static List<KuduContinuousWebJobStatus> ParseStatuses(string json)
        {
            return JsonConvert.DeserializeObject<List<KuduContinuousWebJobStatus>>(json)
                ?? new List<KuduContinuousWebJobStatus>();
        }
    }

    internal sealed class KuduContinuousWebJobStatus
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }
}

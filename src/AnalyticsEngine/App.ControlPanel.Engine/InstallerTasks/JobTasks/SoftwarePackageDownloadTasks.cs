using App.ControlPanel.Engine.Entities;
using CloudInstallEngine;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{

    public class LatestStableSoftwarePackageDownloadTask : ResourceInstallTask<LocalStorageInstallSourceInfo>
    {
        public const string CFG_KEY_RepoOwner = "RepoOwner", CFG_KEY_RepoName = "RepoName";
        private static readonly HttpClient _httpClient = CreateHttpClient();

        public LatestStableSoftwarePackageDownloadTask(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "AnalyticsEngine-Installer");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

            // Anonymous GitHub API requests are limited to 60 per hour PER IP. That is shared by everyone
            // behind the same outbound address - a CI runner pool, or an entire office behind one NAT - so
            // the budget is routinely already spent by the time we ask, and the API answers 403.
            // A token raises the limit to 5,000/hour for whoever supplies one. The installer normally has
            // none (and must keep working without one), but CI does.
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                ?? Environment.GetEnvironmentVariable("GH_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
            }

            return client;
        }

        /// <summary>
        /// Turns a failed GitHub API response into something an admin can act on.
        /// </summary>
        /// <remarks>
        /// A rate-limited response is a bare <c>403 Forbidden</c>, which reads like a permissions problem and
        /// sends people looking for a credential that was never needed. GitHub distinguishes the two in the
        /// headers: on a rate limit <c>x-ratelimit-remaining</c> is 0 and <c>x-ratelimit-reset</c> carries the
        /// unix time it frees up. Say which one it is, and when it will work again.
        /// </remarks>
        private static string DescribeFailedRelease(HttpResponseMessage response, string apiUrl)
        {
            var remaining = FirstHeader(response, "x-ratelimit-remaining");
            var isRateLimited = (int)response.StatusCode == 429
                || (response.StatusCode == System.Net.HttpStatusCode.Forbidden && remaining == "0");

            if (!isRateLimited)
            {
                return $"Failed to fetch latest release from GitHub ({response.StatusCode}). URL: {apiUrl}";
            }

            var resetText = "shortly";
            if (long.TryParse(FirstHeader(response, "x-ratelimit-reset"), out var resetUnix))
            {
                resetText = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime)
                    .ToString("u") + " (UTC)";
            }

            return "GitHub rejected the request because its API rate limit has been reached, not because of a "
                + $"permissions problem. The limit resets at {resetText}. "
                + "Anonymous requests are limited to 60 per hour per public IP address, which is shared by "
                + "everyone on your network, so a busy office or VPN can exhaust it. "
                + "Either wait for the reset and retry, set a GITHUB_TOKEN environment variable to a token "
                + "with public read access, or download the release ZIPs manually and install from local "
                + $"files instead. URL: {apiUrl}";
        }

        private static string FirstHeader(HttpResponseMessage response, string name)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                return values.FirstOrDefault();
            }
            return null;
        }

        public override async Task<LocalStorageInstallSourceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var repoOwner = _config[CFG_KEY_RepoOwner];
            var repoName = _config[CFG_KEY_RepoName];
            var apiUrl = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";

            // Fetch latest release metadata from GitHub
            _logger.LogInformation($"Fetching latest stable release from GitHub repo {repoOwner}/{repoName}...");
            var response = await _httpClient.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new UnexpectedInstallException(DescribeFailedRelease(response, apiUrl));
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JObject.Parse(json);
            var tagName = release["tag_name"]?.ToString();
            var assets = release["assets"] as JArray;
            if (assets == null || !assets.Any())
            {
                throw new UnexpectedInstallException("Latest GitHub release has no assets");
            }

            _logger.LogInformation($"Found latest stable release version '{tagName}' from GitHub repo {repoOwner}/{repoName}");

            // Map expected file names to software components
            var componentFiles = new Dictionary<SoftwareComponent, string>
            {
                { SoftwareComponent.WebJobActivity, InstallerConstants.FILENAME_ZIP_WEBJOB_ACTIVITY },
                { SoftwareComponent.WebJobAppInsights, InstallerConstants.FILENAME_ZIP_WEBJOB_APPINSIGHTS },
                { SoftwareComponent.AITracker, InstallerConstants.FILENAME_ZIP_AITRACKER },
                { SoftwareComponent.ControlPanel, InstallerConstants.FILENAME_ZIP_CONTROL_PANEL },
                { SoftwareComponent.WebSite, InstallerConstants.FILENAME_ZIP_WEBSITE }
            };

            // Build a lookup of asset name -> download URL
            var assetUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                var name = asset["name"]?.ToString();
                var downloadUrl = asset["browser_download_url"]?.ToString();
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(downloadUrl))
                {
                    assetUrls[name] = downloadUrl;
                }
            }

            // Verify all expected assets exist
            foreach (var kvp in componentFiles)
            {
                if (!assetUrls.ContainsKey(kvp.Value))
                {
                    throw new UnexpectedInstallException($"Latest GitHub release is missing expected asset '{kvp.Value}'");
                }
            }

            _logger.LogInformation("Downloading latest stable release...");

            var locallyDownloadedRelease = new LocalStorageInstallSourceInfo();
            var tempDir = StringUtils.TempDirPath;
            Directory.CreateDirectory(tempDir);

            // Download each asset in parallel
            var downloadTasks = new Dictionary<SoftwareComponent, Task<string>>();
            foreach (var kvp in componentFiles)
            {
                var url = assetUrls[kvp.Value];
                var fileName = kvp.Value;
                downloadTasks[kvp.Key] = Task.Run(() => DownloadFileToDir(url, fileName, tempDir));
            }

            await Task.WhenAll(downloadTasks.Values);

            // Build return structure
            foreach (var kvp in downloadTasks)
            {
                locallyDownloadedRelease.GetSolutionComponentLocation(kvp.Key).FileLocation = kvp.Value.Result;
            }

            return locallyDownloadedRelease;
        }

        async Task<string> DownloadFileToDir(string downloadUrl, string fileName, string baseDir)
        {
            if (string.IsNullOrEmpty(downloadUrl))
            {
                throw new ArgumentNullException(nameof(downloadUrl));
            }

            var fileBytes = await _httpClient.GetByteArrayAsync(downloadUrl);

            var filePath = Path.Combine(baseDir, fileName);

            var fileDir = new FileInfo(filePath).Directory;
            if (!fileDir.Exists)
            {
                fileDir.Create();
            }

            File.WriteAllBytes(filePath, fileBytes);
            return filePath;
        }
    }

    public class UseLocalOverrideDownloadTask : ResourceInstallTask<LocalStorageInstallSourceInfo>
    {
        private readonly LocalStorageInstallSourceInfo _localOverrideSources;

        public UseLocalOverrideDownloadTask(LocalStorageInstallSourceInfo localOverrideSources, TaskConfig config, ILogger logger) : base(config, logger)
        {
            _localOverrideSources = localOverrideSources ?? throw new ArgumentNullException(nameof(localOverrideSources));
        }

        public override Task<LocalStorageInstallSourceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            _logger.LogInformation("Using local solution files instead of downloading latest stable release");
            return Task.FromResult(_localOverrideSources);
        }
    }
}

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
            return client;
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
                throw new UnexpectedInstallException($"Failed to fetch latest release from GitHub ({response.StatusCode}). URL: {apiUrl}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JObject.Parse(json);
            var assets = release["assets"] as JArray;
            if (assets == null || !assets.Any())
            {
                throw new UnexpectedInstallException("Latest GitHub release has no assets");
            }

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

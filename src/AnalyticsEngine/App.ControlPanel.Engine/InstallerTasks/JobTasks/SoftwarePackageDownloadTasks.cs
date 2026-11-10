using App.ControlPanel.Engine.Entities;
using CloudInstallEngine;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{

    public class LatestStableSoftwarePackageDownloadTask : ResourceInstallTask<LocalStorageInstallSourceInfo>
    {
        public const string CFG_KEY_RepoOwner = "RepoOwner", CFG_KEY_RepoName = "RepoName";

        /// <summary>
        /// Longest a package download may go without receiving any data. It is the only limit on a package download: the
        /// installer sets no overall one, so a download that keeps receiving data runs for as long as GitHub and the network
        /// allow. HttpClient's default of 100 seconds for the whole transfer failed the packages, tens of megabytes each, on a
        /// slow link.
        /// </summary>
        /// <remarks>
        /// This is not a speed limit: any data resets it, and its clock stops while a chunk is written to disk. Without it, a
        /// connection that drops silently, with no TCP reset (a VPN, proxy or NAT that forgets it), would leave the installer
        /// waiting indefinitely. On .NET Framework an asynchronous read already waiting on the network has no timeout of its own
        /// (<c>HttpWebRequest.ReadWriteTimeout</c> covers only synchronous reads), and only TCP keep-alive would notice the dead
        /// connection - after about two hours with Windows' defaults - but these sockets are opened without it
        /// (<c>ServicePointManager.SetTcpKeepAlive</c> is off by default). The user's Cancel works at any time either way.
        /// </remarks>
        public static readonly TimeSpan PackageDownloadStallTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Longest the GitHub API may take to describe the latest release, and GitHub to send its
        /// <see cref="ReleaseManifestAssetName"/>: HttpClient's default. Both are small, so unlike a package they have an
        /// overall limit.
        /// </summary>
        public static readonly TimeSpan ReleaseMetadataTimeout = TimeSpan.FromSeconds(100);

        /// <summary>The release asset CI publishes with the SHA-256 of every package (<c>.github/scripts/prepare-release.mjs</c>).</summary>
        public const string ReleaseManifestAssetName = "release-manifest.json";

        private static readonly HttpClient _sharedHttpClient = CreateHttpClient(new HttpClientHandler());
        private readonly HttpClient _httpClient;

        public LatestStableSoftwarePackageDownloadTask(TaskConfig config, ILogger logger) : this(config, logger, _sharedHttpClient)
        {
        }

        /// <summary>Test seam: downloads through <paramref name="httpClient"/>, normally one over a fake handler.</summary>
        internal LatestStableSoftwarePackageDownloadTask(TaskConfig config, ILogger logger, HttpClient httpClient) : base(config, logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        internal TimeSpan StallTimeout { get; set; } = PackageDownloadStallTimeout;
        internal TimeSpan MetadataTimeout { get; set; } = ReleaseMetadataTimeout;

        /// <summary>Where the packages are written. Null is the installer's temporary folder.</summary>
        internal string DownloadDirectory { get; set; }

        internal static HttpClient CreateHttpClient(HttpMessageHandler handler)
        {
            // The client's single whole-request timeout would cut a long package download short. A package has only the
            // no-data limit, and the release metadata and manifest requests carry their own limit (the timeouts above).
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
            string json;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken))
            {
                timeout.CancelAfter(MetadataTimeout);
                try
                {
                    using (var response = await _httpClient.GetAsync(apiUrl, timeout.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new UnexpectedInstallException(DescribeFailedRelease(response, apiUrl));
                        json = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (OperationCanceledException) when (!CancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new UnexpectedInstallException($"GitHub did not describe the latest release within {DescribeDuration(MetadataTimeout)}. " +
                        "Check that this machine can reach api.github.com, through the proxy configured in the installer if there is one, " +
                        $"and retry, or download the release ZIPs manually and install from local files instead. URL: {apiUrl}");
                }
            }
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

            // Every package is checked against the SHA-256 the release build published before anything runs or deploys it:
            // the control-panel app among them is run on this machine with the SQL administrator password or the installer's
            // client secret. So a release that can't say what its packages should be is refused before they are downloaded.
            if (!assetUrls.TryGetValue(ReleaseManifestAssetName, out var manifestUrl))
            {
                throw new UnexpectedInstallException($"GitHub release '{tagName}' has no {ReleaseManifestAssetName}, which holds the SHA-256 " +
                    "of each package, so its packages can't be verified. " + UnverifiedPackagesAdvice);
            }
            var expectedHashes = await DownloadReleaseManifestAsync(manifestUrl, tagName, componentFiles.Values);

            _logger.LogInformation("Downloading latest stable release...");

            var locallyDownloadedRelease = new LocalStorageInstallSourceInfo();
            var tempDir = DownloadDirectory ?? StringUtils.TempDirPath;
            Directory.CreateDirectory(tempDir);

            // Download each asset, two at a time
            var downloadTasks = new Dictionary<SoftwareComponent, Task<string>>();
            using (var slots = new SemaphoreSlim(ParallelDownloads))
            {
                foreach (var kvp in componentFiles)
                {
                    var url = assetUrls[kvp.Value];
                    var fileName = kvp.Value;
                    downloadTasks[kvp.Key] = DownloadWhenASlotIsFree(slots, url, fileName, tempDir, expectedHashes[fileName]);
                }

                try
                {
                    await Task.WhenAll(downloadTasks.Values);
                }
                catch (Exception) when (CancellationToken.IsCancellationRequested)
                {
                    var unfinished = componentFiles.Where(c => downloadTasks[c.Key].Status != TaskStatus.RanToCompletion).Select(c => c.Value);
                    _logger.LogInformation($"Release download cancelled by the user before {string.Join(", ", unfinished)} finished.");
                    CancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (Exception)
                {
                    // Every package's own reason, not just the first: on a slow link several fail together.
                    var reasons = downloadTasks.Values.Where(t => t.IsFaulted).Select(t => t.Exception.GetBaseException().Message).ToList();
                    throw new UnexpectedInstallException($"Could not download {reasons.Count} of {downloadTasks.Count} packages of GitHub release " +
                        $"'{tagName}': {string.Join("; ", reasons)}. Retry, or download the release ZIPs manually and install from local files instead.");
                }
            }

            // Build return structure
            foreach (var kvp in downloadTasks)
            {
                locallyDownloadedRelease.GetSolutionComponentLocation(kvp.Key).FileLocation = kvp.Value.Result;
            }

            return locallyDownloadedRelease;
        }

        /// <summary>
        /// How many packages download at once. .NET Framework opens at most two connections to a server
        /// (<c>ServicePointManager.DefaultConnectionLimit</c>), so a third request would only queue - with its no-data clock
        /// running while it waited, which on a slow link would fail it for a stall that never happened.
        /// </summary>
        internal const int ParallelDownloads = 2;

        async Task<string> DownloadWhenASlotIsFree(SemaphoreSlim slots, string downloadUrl, string fileName, string baseDir, string expectedSha256)
        {
            await slots.WaitAsync(CancellationToken);
            try
            {
                return await DownloadFileToDir(downloadUrl, fileName, baseDir, expectedSha256);
            }
            finally
            {
                slots.Release();
            }
        }

        /// <summary>
        /// Streams one package to disk and checks it against <paramref name="expectedSha256"/>. There is no overall limit: it
        /// gives up only after <see cref="StallTimeout"/> without data, or when the user cancels. A failure is a
        /// <see cref="PackageDownloadFailedException"/> that names the package and says whether it timed out, and leaves no
        /// file behind; the user's cancel is an <see cref="OperationCanceledException"/>.
        /// </summary>
        async Task<string> DownloadFileToDir(string downloadUrl, string fileName, string baseDir, string expectedSha256)
        {
            if (string.IsNullOrEmpty(downloadUrl))
            {
                throw new ArgumentNullException(nameof(downloadUrl));
            }

            var filePath = Path.Combine(baseDir, fileName);
            long received = 0;
            using (var stall = new CancellationTokenSource(StallTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, stall.Token))
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                try
                {
                    using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, linked.Token))
                    // On .NET Framework a read already waiting on the network ignores the token; disposing the response ends it.
                    using (linked.Token.Register(response.Dispose))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new PackageDownloadFailedException(
                                $"'{fileName}' failed: GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase})");
                        }

                        using (var source = await response.Content.ReadAsStreamAsync())
                        using (var target = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            var buffer = new byte[81920];
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, linked.Token)) > 0)
                            {
                                // The stall limit is for the network, so its clock stops while a chunk is written: a disk slowed
                                // by, say, an antivirus scan must not be reported as a connection that went quiet.
                                stall.CancelAfter(Timeout.InfiniteTimeSpan);
                                sha256.AppendData(buffer, 0, read);
                                await target.WriteAsync(buffer, 0, read, linked.Token);
                                received += read;
                                stall.CancelAfter(StallTimeout);
                            }
                        }
                    }

                    var actualSha256 = ToHex(sha256.GetHashAndReset());
                    if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PackageDownloadFailedException($"'{fileName}' is not the file the release build published: " +
                            $"its SHA-256 is {actualSha256}, and {ReleaseManifestAssetName} says {expectedSha256.ToLowerInvariant()}. It was " +
                            "deleted without being run or deployed. If this happens again, something between this machine and GitHub is " +
                            "changing the download");
                    }
                    return filePath;
                }
                catch (Exception ex)
                {
                    TryDelete(filePath);
                    CancellationToken.ThrowIfCancellationRequested();
                    if (ex is PackageDownloadFailedException) throw;
                    if (stall.IsCancellationRequested)
                    {
                        throw new PackageDownloadFailedException($"'{fileName}' timed out: no data arrived for " +
                            $"{DescribeDuration(StallTimeout)} ({DescribeSize(received)} received)");
                    }
                    var cause = ex.GetBaseException();
                    throw new PackageDownloadFailedException($"'{fileName}' failed: {cause.GetType().Name}: {cause.Message}");
                }
            }
        }

        static void TryDelete(string filePath)
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        const string UnverifiedPackagesAdvice = "The installer won't run or deploy packages it can't check against the hashes the " +
            "release build published. Retry, or download the release ZIPs manually and install from local files instead.";

        /// <summary>
        /// Reads the SHA-256 of each of <paramref name="packages"/> from the release's manifest. Refuses, naming the package,
        /// when one has no hash: it could not be verified.
        /// </summary>
        async Task<Dictionary<string, string>> DownloadReleaseManifestAsync(string manifestUrl, string tagName, IEnumerable<string> packages)
        {
            string json;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken))
            {
                timeout.CancelAfter(MetadataTimeout);
                try
                {
                    using (var response = await _httpClient.GetAsync(manifestUrl, timeout.Token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new UnexpectedInstallException($"Could not download {ReleaseManifestAssetName} of GitHub release '{tagName}': " +
                                $"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}). " + UnverifiedPackagesAdvice);
                        }
                        json = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (OperationCanceledException) when (!CancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new UnexpectedInstallException($"{ReleaseManifestAssetName} of GitHub release '{tagName}' did not download within " +
                        $"{DescribeDuration(MetadataTimeout)}. " + UnverifiedPackagesAdvice);
                }
            }

            Dictionary<string, string> hashes;
            try
            {
                hashes = ParseReleaseManifest(json);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new UnexpectedInstallException($"{ReleaseManifestAssetName} of GitHub release '{tagName}' can't be read ({ex.Message}). " +
                    UnverifiedPackagesAdvice);
            }

            foreach (var package in packages)
            {
                if (!hashes.TryGetValue(package, out var hash) || !IsSha256(hash))
                {
                    throw new UnexpectedInstallException($"{ReleaseManifestAssetName} of GitHub release '{tagName}' has no SHA-256 for " +
                        $"'{package}', so it can't be verified. " + UnverifiedPackagesAdvice);
                }
            }
            return hashes;
        }

        /// <summary>
        /// Package name to SHA-256 (hex), from the manifest <c>.github/scripts/prepare-release.mjs</c> writes:
        /// <c>{ "commit": ..., "assets": [ { "name": "ControlPanelApp.zip", "sha256": "..." }, ... ] }</c>.
        /// </summary>
        static Dictionary<string, string> ParseReleaseManifest(string json)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (JObject.Parse(json)["assets"] is JArray assets)
            {
                foreach (var asset in assets.OfType<JObject>())
                {
                    var name = asset["name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) hashes[name] = asset["sha256"]?.ToString();
                }
            }
            return hashes;
        }

        static bool IsSha256(string hex) => hex != null && hex.Length == 64 && hex.All(Uri.IsHexDigit);

        static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

        static string DescribeDuration(TimeSpan duration)
        {
            if (duration.TotalMinutes >= 1 && duration.Seconds == 0 && duration.Milliseconds == 0)
            {
                return duration.TotalMinutes == 1 ? "1 minute" : duration.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " minutes";
            }
            if (duration.TotalSeconds >= 1)
            {
                return duration.TotalSeconds == 1 ? "1 second" : duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " seconds";
            }
            return duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        static string DescribeSize(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>One release package that could not be downloaded. The message names the package and says why.</summary>
    internal sealed class PackageDownloadFailedException : UnexpectedInstallException
    {
        public PackageDownloadFailedException(string message) : base(message)
        {
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

using App.ControlPanel.Engine.Models;
using Common.Entities;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UpdateCheck
{
    /// <summary>
    /// Answers "is there a newer release than the build we're running?" by comparing
    /// <see cref="BuildConstants.BuildLabel"/> against the latest published release on GitHub.
    /// </summary>
    /// <remarks>
    /// Read-only and best-effort. It never throws at the caller: a deployment with no outbound internet
    /// (very common - private endpoints, locked-down egress) must still be able to open the page and see
    /// which build it is on, with an explanation of why the remote half is missing.
    /// </remarks>
    public static class UpdateChecker
    {
        /// <summary>
        /// GitHub's <c>releases/latest</c> deliberately excludes drafts and pre-releases, so this only ever
        /// compares against a published stable release - which is exactly what we want to offer an admin.
        /// </summary>
        private static readonly string _apiUrl =
            $"https://api.github.com/repos/{SoftwareReleaseConfig.GITHUB_REPO_OWNER}/{SoftwareReleaseConfig.GITHUB_REPO_NAME}/releases/latest";

        private const string CacheKey = "UpdateCheck::LatestRelease";

        /// <summary>
        /// How long a GitHub answer is reused. Anonymous GitHub API calls are limited to 60/hour per public
        /// IP, shared by everything behind the same outbound address. Releases appear every few weeks, so
        /// caching costs the admin nothing and stops a handful of impatient clicks burning the budget for
        /// the whole tenant (including the installer, which needs the same API to download a release).
        /// </summary>
        private static readonly TimeSpan _cacheFor = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Short by design: this runs while an admin waits on a button press, and on a deployment with
        /// blocked egress the connection typically hangs rather than refusing, so an untimed call would
        /// leave the page spinning until IIS gave up.
        /// </summary>
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = _timeout };
            // GitHub rejects requests with no User-Agent outright.
            client.DefaultRequestHeaders.Add("User-Agent", "Microsoft365-Analytics-Insights-Portal");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            return client;
        }

        /// <summary>
        /// Compares the running build against the latest published release. Never throws.
        /// </summary>
        public static async Task<UpdateCheckApiModel> CheckAsync()
        {
            var currentLabel = BuildConstants.BuildLabel;
            var result = new UpdateCheckApiModel
            {
                CurrentBuildLabel = currentLabel,
                IsDevBuild = BuildVersion.IsDevBuild(currentLabel),
                CurrentBuild = BuildVersion.TryParseBuildNumber(currentLabel),
                CheckedAtUtc = DateTime.UtcNow,
            };

            var latest = await GetLatestReleaseAsync();

            result.CheckedAtUtc = latest.FetchedAtUtc;
            if (latest.Error != null)
            {
                result.CheckError = latest.Error;
                return result;
            }

            result.LatestBuild = latest.Build;
            result.LatestReleaseName = latest.Name;
            result.LatestReleaseUrl = latest.Url;
            result.LatestPublishedUtc = latest.PublishedUtc;

            if (result.IsDevBuild)
            {
                result.CheckError = "This is a locally-compiled build (DEV_BUILD), so it has no build number to "
                    + "compare. The latest published release is shown for reference.";
                return result;
            }

            if (!result.CurrentBuild.HasValue)
            {
                result.CheckError = $"Couldn't read a build number out of this build's label ('{currentLabel}'), "
                    + "so it can't be compared. The latest published release is shown for reference.";
                return result;
            }

            if (!result.LatestBuild.HasValue)
            {
                result.CheckError = "Couldn't read a build number from the latest GitHub release, so the two "
                    + "can't be compared. Open the release page to check manually.";
                return result;
            }

            result.UpdateAvailable = BuildVersion.IsUpdateAvailable(result.CurrentBuild, result.LatestBuild);
            return result;
        }

        private class LatestRelease
        {
            public int? Build { get; set; }
            public string Name { get; set; }
            public string Url { get; set; }
            public DateTime? PublishedUtc { get; set; }
            public string Error { get; set; }
            public DateTime FetchedAtUtc { get; set; }
        }

        private static async Task<LatestRelease> GetLatestReleaseAsync()
        {
            if (MemoryCache.Default[CacheKey] is LatestRelease cached)
            {
                return cached;
            }

            var fetched = await FetchLatestReleaseAsync();

            // Cache failures too, for the same window. A blocked-egress deployment would otherwise pay the
            // full timeout on every click, and a rate-limited one would keep asking while still limited.
            MemoryCache.Default.Set(CacheKey, fetched, DateTimeOffset.UtcNow.Add(_cacheFor));
            return fetched;
        }

        private static async Task<LatestRelease> FetchLatestReleaseAsync()
        {
            var result = new LatestRelease { FetchedAtUtc = DateTime.UtcNow };

            try
            {
                using (var response = await _httpClient.GetAsync(_apiUrl))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        result.Error = DescribeFailure(response);
                        return result;
                    }

                    var release = JObject.Parse(await response.Content.ReadAsStringAsync());
                    result.Build = BuildVersion.TryParseBuildNumber(release["tag_name"]?.ToString());
                    result.Name = release["name"]?.ToString();
                    result.Url = release["html_url"]?.ToString();

                    if (DateTime.TryParse(release["published_at"]?.ToString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var published))
                    {
                        result.PublishedUtc = published;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // HttpClient surfaces its own timeout as a cancellation, which reads like the user cancelled.
                result.Error = $"Timed out after {_timeout.TotalSeconds:0}s contacting github.com. If this web app "
                    + "has no outbound internet access (for example a private-endpoint deployment with restricted "
                    + "egress), update checks can't work from here - check the release page manually instead.";
            }
            catch (HttpRequestException ex)
            {
                result.Error = $"Couldn't reach github.com to check for updates: {InnerMostMessage(ex)}. This is "
                    + "expected if the web app has no outbound internet access; check the release page manually.";
            }
            catch (Exception ex)
            {
                result.Error = $"Update check failed: {InnerMostMessage(ex)}";
            }

            return result;
        }

        /// <summary>
        /// Turns a failed GitHub response into something an admin can act on.
        /// </summary>
        /// <remarks>
        /// A rate-limited response is a bare <c>403 Forbidden</c>, which reads like a permissions problem and
        /// sends people hunting for a credential that was never needed. GitHub distinguishes the two in the
        /// headers: on a rate limit <c>x-ratelimit-remaining</c> is 0 and <c>x-ratelimit-reset</c> carries the
        /// unix time it frees up. Mirrors the installer's handling in
        /// <c>LatestStableSoftwarePackageDownloadTask</c>.
        /// </remarks>
        private static string DescribeFailure(HttpResponseMessage response)
        {
            var remaining = FirstHeader(response, "x-ratelimit-remaining");
            var isRateLimited = (int)response.StatusCode == 429
                || (response.StatusCode == HttpStatusCode.Forbidden && remaining == "0");

            if (isRateLimited)
            {
                var resetText = "shortly";
                if (long.TryParse(FirstHeader(response, "x-ratelimit-reset"), out var resetUnix))
                {
                    resetText = DateTimeOffset.FromUnixTimeSeconds(resetUnix).UtcDateTime.ToString("u");
                }

                return "GitHub rejected the request because its API rate limit has been reached, not because of a "
                    + $"permissions problem. The limit resets at {resetText}. Anonymous requests are limited to 60 "
                    + "per hour per public IP address, which is shared by everything behind your outbound address. "
                    + "Try again after the reset.";
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return "GitHub returned 404 for the releases endpoint. If this deployment sits behind a proxy that "
                    + "intercepts HTTPS, it may be returning its own response rather than GitHub's.";
            }

            return $"GitHub returned {(int)response.StatusCode} ({response.StatusCode}) when asked for the latest release.";
        }

        private static string FirstHeader(HttpResponseMessage response, string name)
        {
            return response.Headers.TryGetValues(name, out var values)
                ? System.Linq.Enumerable.FirstOrDefault(values)
                : null;
        }

        /// <summary>The useful message is usually on the inner-most exception, not the wrapper.</summary>
        private static string InnerMostMessage(Exception ex)
        {
            while (ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex.Message;
        }
    }
}

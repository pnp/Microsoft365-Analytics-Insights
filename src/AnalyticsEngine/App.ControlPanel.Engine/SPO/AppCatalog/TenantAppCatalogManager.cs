using App.ControlPanel.Engine.SPO.Auth;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.AppCatalog
{
    /// <summary>
    /// Uploads &amp; tenant-deploys an SPFx package (.sppkg) to the SharePoint Online tenant app catalog.
    ///
    /// Replaces <c>OfficeDevPnP.Core.ALM.AppManager</c>, which was the only other thing the installer used that
    /// library for. The underlying operation is just the app-catalog REST API, which CSOM doesn't surface, so
    /// there's no need to take a dependency on a provisioning framework to do it.
    /// </summary>
    public class TenantAppCatalogManager : IDisposable
    {
        const string ACCEPT_JSON = "application/json;odata=nometadata";

        readonly ISpoAuthenticator _authenticator;
        readonly ILogger _logger;
        readonly HttpClient _httpClient;
        readonly bool _ownsHttpClient;

        public TenantAppCatalogManager(ISpoAuthenticator authenticator, ILogger logger)
            : this(authenticator, logger, NewHttpClient(), ownsHttpClient: true)
        {
        }

        public TenantAppCatalogManager(ISpoAuthenticator authenticator, ILogger logger, HttpClient httpClient)
            : this(authenticator, logger, httpClient, ownsHttpClient: false)
        {
        }

        TenantAppCatalogManager(ISpoAuthenticator authenticator, ILogger logger, HttpClient httpClient, bool ownsHttpClient)
        {
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = ownsHttpClient;
        }

        static HttpClient NewHttpClient()
        {
            // SPFx packages are small, but app-catalog processing on the SPO side can be slow.
            return new HttpClient { Timeout = TimeSpan.FromSeconds(200) };
        }

        /// <summary>
        /// How long to wait between checks that an uploaded package has become available. Overridable so
        /// tests don't have to actually sleep.
        /// </summary>
        public TimeSpan AvailabilityRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Uploads the package to the tenant app catalog, replacing any existing version.
        /// </summary>
        /// <returns>The unique ID of the uploaded app, needed to deploy it.</returns>
        public async Task<Guid> AddAsync(string appCatalogUrl, string packagePath, bool overwrite = true)
        {
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException($"SPFx package '{packagePath}' not found", packagePath);
            }

            var fileName = new FileInfo(packagePath).Name;
            var fileBytes = File.ReadAllBytes(packagePath);

            var url = $"{appCatalogUrl.TrimEnd('/')}/_api/web/tenantappcatalog/Add(overwrite={overwrite.ToString().ToLowerInvariant()}, url='{Uri.EscapeDataString(fileName)}')";

            string responseBody;
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Add("binaryStringRequestBody", "true");
                request.Content = new ByteArrayContent(fileBytes);

                responseBody = await SendAsync(request, appCatalogUrl, $"upload '{fileName}' to the app catalog");
            }

            string uniqueId;
            try
            {
                uniqueId = JObject.Parse(responseBody)["UniqueId"]?.ToString();
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new SpoAppCatalogException($"The app catalog returned an unreadable response for '{fileName}': {responseBody}", ex);
            }

            if (!Guid.TryParse(uniqueId, out var appId))
            {
                throw new SpoAppCatalogException($"The app catalog accepted '{fileName}' but didn't return an app ID. Response: {responseBody}");
            }

            _logger.LogInformation($"Uploaded '{fileName}' to app catalog '{appCatalogUrl}' (app ID {appId}).");

            // SharePoint can acknowledge the upload before the package shows up under AvailableApps, and
            // Deploy against a not-yet-available app 404s. Wait for it, as OfficeDevPnP's AppManager did.
            await WaitUntilAvailableAsync(appCatalogUrl, appId);

            return appId;
        }

        /// <summary>
        /// Polls until the uploaded package appears under AvailableApps, so the subsequent Deploy doesn't
        /// race SharePoint's asynchronous app-catalog processing.
        /// </summary>
        async Task WaitUntilAvailableAsync(string appCatalogUrl, Guid appId)
        {
            const int maxAttempts = 5;
            var waitBetweenAttempts = AvailabilityRetryDelay;
            var url = $"{appCatalogUrl.TrimEnd('/')}/_api/web/tenantappcatalog/AvailableApps/GetById('{appId}')";

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("accept", ACCEPT_JSON);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(appCatalogUrl));

                    HttpResponseMessage response;
                    try
                    {
                        response = await _httpClient.SendAsync(request);
                    }
                    catch (Exception ex) when (IsTransportFailure(ex))
                    {
                        throw new SpoAppCatalogException($"Couldn't reach the app catalog at '{appCatalogUrl}' while waiting for app {appId} to become available. {ex.Message}", ex);
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        var body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync();
                        throw new SpoAppCatalogException(
                            $"Couldn't read app {appId} back from '{appCatalogUrl}' - SharePoint returned {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
                    }
                }

                if (attempt < maxAttempts)
                {
                    _logger.LogInformation($"App {appId} isn't available in the catalog yet - waiting {waitBetweenAttempts.TotalSeconds:N0}s (attempt {attempt}/{maxAttempts})...");
                    await Task.Delay(waitBetweenAttempts);
                }
            }

            throw new SpoAppCatalogException(
                $"App {appId} was uploaded to '{appCatalogUrl}' but SharePoint hadn't made it available after " +
                $"{maxAttempts * waitBetweenAttempts.TotalSeconds:N0} seconds, so it could not be deployed. Deploy it manually from the app catalog.");
        }

        /// <summary>
        /// Deploys (trusts) an uploaded app so it is available tenant-wide.
        /// </summary>
        /// <param name="skipFeatureDeployment">
        /// True makes the extension available to every site without a per-site install. The AITracker extension is
        /// still only activated on the site collections the installer explicitly stapled it to.
        /// </param>
        public async Task DeployAsync(string appCatalogUrl, Guid appId, bool skipFeatureDeployment = true)
        {
            var url = $"{appCatalogUrl.TrimEnd('/')}/_api/web/tenantappcatalog/AvailableApps/GetById('{appId}')/Deploy";

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                var body = new JObject { ["skipFeatureDeployment"] = skipFeatureDeployment }.ToString();
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ACCEPT_JSON + ";charset=utf-8");

                await SendAsync(request, appCatalogUrl, $"deploy app {appId}");
            }

            _logger.LogInformation($"Deployed SPFx extension {appId} to the tenant from '{appCatalogUrl}'.");
        }

        async Task<string> GetTokenAsync(string appCatalogUrl)
        {
            try
            {
                return await _authenticator.GetAccessTokenAsync(appCatalogUrl);
            }
            catch (SpoAuthenticationException)
            {
                // Sign-in problems are the admin's to fix and must abort the install, not fall through
                // to the "deploy it by hand" path.
                throw;
            }
        }

        /// <summary>
        /// Network-level and timeout failures. These are ordinary app-catalog problems that should leave the
        /// admin with the "do this step manually" fallback, so they're surfaced as <see cref="SpoAppCatalogException"/>
        /// rather than escaping and aborting the whole SharePoint install.
        /// </summary>
        static bool IsTransportFailure(Exception ex)
        {
            return ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException || ex is IOException;
        }

        async Task<string> SendAsync(HttpRequestMessage request, string appCatalogUrl, string operationDescription)
        {
            request.Headers.Add("accept", ACCEPT_JSON);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(appCatalogUrl));

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                throw new SpoAppCatalogException(
                    $"Couldn't {operationDescription} at '{appCatalogUrl}' - the request failed or timed out. {ex.Message}", ex);
            }

            var responseBody = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var hint = string.Empty;
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    hint = " The signed-in account needs to be a SharePoint administrator to upload and deploy to the tenant app catalog.";
                }
                throw new SpoAppCatalogException(
                    $"Couldn't {operationDescription} at '{appCatalogUrl}' - SharePoint returned {(int)response.StatusCode} {response.ReasonPhrase}.{hint} {responseBody}");
            }
            return responseBody;
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient?.Dispose();
            }
        }
    }

    /// <summary>
    /// The SharePoint tenant app catalog rejected an upload or deploy request.
    /// </summary>
    public class SpoAppCatalogException : Exception
    {
        public SpoAppCatalogException(string message) : base(message) { }
        public SpoAppCatalogException(string message, Exception innerException) : base(message, innerException) { }
    }
}

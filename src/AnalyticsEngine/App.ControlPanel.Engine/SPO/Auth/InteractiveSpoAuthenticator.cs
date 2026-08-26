using App.ControlPanel.Engine.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SPO.Auth
{
    /// <summary>
    /// Interactive (delegated) SharePoint Online sign-in via MSAL &amp; the admin's default system browser.
    ///
    /// Replaces OfficeDevPnP.Core's <c>GetWebLoginClientContext</c>, which used an embedded Internet Explorer
    /// popup and cookie authentication. That library only ever shipped a net461 build, so it blocked moving the
    /// installer off .NET Framework; MSAL plus a bearer token on the SharePoint REST calls is portable and
    /// works unchanged on modern .NET.
    ///
    /// One instance signs in once and is then reused for every target site and for the app catalog, because a
    /// SharePoint access token is scoped to the whole SPO tenant, not to an individual site collection.
    /// </summary>
    public class InteractiveSpoAuthenticator : ISpoAuthenticator
    {
        /// <summary>
        /// Client ID of Microsoft's first-party "SharePoint Online Management Shell" application. It already has
        /// the localhost reply URLs, public-client flows and SharePoint delegated permissions configured, so an
        /// admin can sign in without registering anything. Overridable for tenants that block it.
        /// </summary>
        public const string CLIENTID_SPO_MANAGEMENT_SHELL = "9bc3ab49-b65d-410a-85ad-de819febfddc";

        /// <summary>
        /// Authority tenant used when the installer config doesn't name one. "organizations" accepts any
        /// work/school account; the resulting token is still scoped to the SPO tenant being addressed.
        /// </summary>
        public const string DEFAULT_AUTHORITY_TENANT = "organizations";

        /// <summary>Re-acquire a token this far ahead of its expiry so long installs don't fail mid-way.</summary>
        static readonly TimeSpan TOKEN_REFRESH_WINDOW = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long to wait for the admin to finish signing in before giving up. Without this the install
        /// blocks forever if the browser never opened, or was closed before sign-in completed.
        /// </summary>
        public static readonly TimeSpan InteractiveSignInTimeout = TimeSpan.FromMinutes(10);

        readonly IPublicClientApplication _app;
        readonly ILogger _logger;
        readonly Dictionary<string, AuthenticationResult> _tokensByResource = new Dictionary<string, AuthenticationResult>(StringComparer.OrdinalIgnoreCase);
        readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);
        bool _signedInMessageLogged = false;

        public InteractiveSpoAuthenticator(SharePointInstallConfig config, ILogger logger)
            : this(config?.AuthClientId, config?.AuthTenantId, logger)
        {
        }

        public InteractiveSpoAuthenticator(string clientId, string tenantId, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? CLIENTID_SPO_MANAGEMENT_SHELL : clientId.Trim();
            var effectiveTenantId = string.IsNullOrWhiteSpace(tenantId) ? DEFAULT_AUTHORITY_TENANT : tenantId.Trim();

            UsingDefaultClientId = effectiveClientId == CLIENTID_SPO_MANAGEMENT_SHELL;

            _app = PublicClientApplicationBuilder.Create(effectiveClientId)
                .WithAuthority($"https://login.microsoftonline.com/{effectiveTenantId}")

                // No port: MSAL binds a free loopback port for the reply. Both this and the "nativeclient" URI
                // are already registered on the SPO Management Shell app.
                .WithRedirectUri("http://localhost")
                .Build();
        }

        /// <summary>
        /// True when no custom app registration was configured, so we're relying on the SharePoint Online
        /// Management Shell app. Used to give admins a targeted error if their tenant has blocked it.
        /// </summary>
        public bool UsingDefaultClientId { get; }

        /// <summary>
        /// Token resource for a site URL. SharePoint issues one token per tenant host, so
        /// https://contoso.sharepoint.com/sites/a and .../sites/b share a token.
        /// </summary>
        static string GetResourceUri(string siteUrl)
        {
            if (string.IsNullOrWhiteSpace(siteUrl))
            {
                throw new ArgumentException("A SharePoint site URL is required", nameof(siteUrl));
            }
            if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException($"'{siteUrl}' is not a valid absolute SharePoint URL", nameof(siteUrl));
            }
            return $"{uri.Scheme}://{uri.Authority}";
        }

        public async Task<string> GetAccessTokenAsync(string siteUrl)
        {
            var resourceUri = GetResourceUri(siteUrl);

            await _tokenLock.WaitAsync();
            try
            {
                if (_tokensByResource.TryGetValue(resourceUri, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.Add(TOKEN_REFRESH_WINDOW))
                {
                    return cached.AccessToken;
                }

                var scopes = new[] { $"{resourceUri}/.default" };
                var result = await AcquireAsync(scopes, resourceUri);

                _tokensByResource[resourceUri] = result;

                if (!_signedInMessageLogged)
                {
                    _logger.LogInformation($"Signed in to SharePoint Online as '{result.Account?.Username}'.");
                    _signedInMessageLogged = true;
                }
                return result.AccessToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        async Task<AuthenticationResult> AcquireAsync(string[] scopes, string resourceUri)
        {
            var account = (await _app.GetAccountsAsync()).FirstOrDefault();
            if (account != null)
            {
                try
                {
                    // Already signed in for another site/the app catalog - no second browser prompt.
                    return await _app.AcquireTokenSilent(scopes, account).ExecuteAsync();
                }
                catch (MsalUiRequiredException)
                {
                    // Fall through to interactive.
                }
            }

            _logger.LogInformation($"Opening your web browser to sign in to '{resourceUri}'. " +
                "Use an account that is a SharePoint administrator (for the app catalog) and a site-collection administrator on each target site.");

            var request = _app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(BrowserPageOptions());
            if (account == null)
            {
                // First sign-in of the run - let the admin pick which account, rather than silently
                // reusing whatever the browser last signed in with.
                request = request.WithPrompt(Prompt.SelectAccount);
            }

            try
            {
                using (var timeout = new CancellationTokenSource(InteractiveSignInTimeout))
                {
                    return await request.ExecuteAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException ex)
            {
                throw new SpoAuthenticationException(
                    $"Timed out after {InteractiveSignInTimeout.TotalMinutes:N0} minutes waiting for the SharePoint browser sign-in to finish, " +
                    "so the SharePoint web components were not installed. This usually means the browser never opened, or the sign-in tab was " +
                    "closed before it completed. Re-run the installer - everything created in Azure so far is left in place and will be reused - " +
                    "and if no browser window appears, copy the sign-in URL from the log above into a browser manually.", ex);
            }
            catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
            {
                throw new SpoAuthenticationException(
                    "SharePoint sign-in was cancelled, so the SharePoint web components could not be installed. " +
                    "Re-run the installer and complete the browser sign-in, or clear 'Track web traffic' on the Targets tab " +
                    "if you don't want SharePoint page tracking.", ex);
            }
            catch (MsalServiceException ex) when (UsingDefaultClientId && IsConsentOrBlockedAppError(ex))
            {
                throw new SpoAuthenticationException(
                    "Sign-in was refused for the built-in 'SharePoint Online Management Shell' application " +
                    $"({CLIENTID_SPO_MANAGEMENT_SHELL}). Either grant it admin consent in your tenant, or register your own " +
                    "Entra ID app (public client, reply URL 'http://localhost', delegated SharePoint permission " +
                    "'AllSites.FullControl') and enter its client ID on the installer's SharePoint tab. " +
                    $"Original error: {ex.Message}", ex);
            }
            catch (MsalServiceException ex) when (!UsingDefaultClientId && (ex.Message ?? string.Empty).Contains("AADSTS50194"))
            {
                throw new SpoAuthenticationException(
                    "Your SharePoint sign-in application is registered as single-tenant, so it can't be used with the " +
                    "multi-tenant sign-in endpoint. Enter your tenant (directory ID or domain) next to the sign-in app ID " +
                    $"on the installer's SharePoint tab. Original error: {ex.Message}", ex);
            }
        }

        static bool IsConsentOrBlockedAppError(MsalServiceException ex)
        {
            // AADSTS65001 = no consent, AADSTS7000112 = app disabled, AADSTS700016 = app not found in tenant,
            // AADSTS50105 = user not assigned to the app.
            var code = ex.Message ?? string.Empty;
            return code.Contains("AADSTS65001") || code.Contains("AADSTS7000112")
                || code.Contains("AADSTS700016") || code.Contains("AADSTS50105");
        }

        /// <summary>
        /// Replaces the pages MSAL shows in the browser once sign-in finishes, and makes sure the sign-in URL
        /// is written to the install log. The stock success page warns against sharing the page or taking
        /// screenshots, which reads alarmingly for what is a routine step in an install, and it doesn't say
        /// what happens next. Logging the URL matters more: if the browser fails to launch, the admin would
        /// otherwise have no way to complete sign-in and no way to recover a long-running install.
        /// </summary>
        SystemWebViewOptions BrowserPageOptions()
        {
            return new SystemWebViewOptions
            {
                OpenBrowserAsync = url =>
                {
                    _logger.LogInformation("If your browser doesn't open automatically, sign in by pasting this address into it: " + url);

                    // Same as MSAL's default: hand the URL to the OS so the admin's *default* browser opens it
                    // (they may well already be signed in there). Don't force a specific browser.
                    Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                    return Task.CompletedTask;
                },

                HtmlMessageSuccess = Page(
                    "Signed in",
                    "<p>You're signed in to SharePoint. You can close this tab.</p>" +
                    "<p>The installer is carrying on with the SharePoint setup &ndash; switch back to it to watch progress.</p>"),

                HtmlMessageError = Page(
                    "Sign-in didn't complete",
                    "<p>SharePoint sign-in was not completed, so the installer could not continue with the SharePoint setup.</p>" +
                    "<p>Close this tab and check the installer window &ndash; it will explain what to do next.</p>" +
                    "<p style=\"color:#605e5c\">Details: {0} &ndash; {1}</p>")
            };
        }

        static string Page(string heading, string bodyHtml)
        {
            return "<html><head><meta charset=\"utf-8\" /><title>" + heading + "</title></head>" +
                   "<body style=\"font-family:Segoe UI,Arial,sans-serif;margin:3em auto;max-width:34em;color:#323130\">" +
                   "<h2 style=\"font-weight:600\">" + heading + "</h2>" +
                   bodyHtml +
                   "<p style=\"color:#605e5c;font-size:.9em\">Microsoft 365 Advanced Analytics installer</p>" +
                   "</body></html>";
        }

        public void Dispose()
        {
            _tokenLock.Dispose();
        }
    }

    /// <summary>
    /// Sign-in to SharePoint Online failed in a way the admin needs to act on.
    /// </summary>
    public class SpoAuthenticationException : Exception
    {
        public SpoAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
    }
}

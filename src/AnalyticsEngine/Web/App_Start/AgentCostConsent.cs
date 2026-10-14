using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb
{
    /// <summary>A separately challenged v2 OIDC scheme: ordinary sign-in and Teams consent are unchanged.</summary>
    public static class AgentCostConsent
    {
        public const string AuthenticationType = "AgentCostConnection";
        public const string ReturnRoute = "/#/admin/agent-cost-connection";
        public const string ObjectIdProperty = "billing.oid";
        public const string TenantProperty = "billing.tid";
        public const string ExpiresProperty = "billing.expires";
        private const string TicketPurpose = "AgentCostConnectIntent.v1";
        internal static HttpClient VerificationClient = new HttpClient();

        public static string ObjectId(ClaimsPrincipal user) => user?.FindFirst("oid")?.Value
            ?? user?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        public static string TenantId(ClaimsPrincipal user) => user?.FindFirst("tid")?.Value
            ?? user?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        public static AuthenticationProperties PropertiesFor(ClaimsPrincipal user)
        {
            if (!PortalAccessPolicy.FromAppSettings().Evaluate(user).Administration
                || string.IsNullOrEmpty(ObjectId(user)) || string.IsNullOrEmpty(TenantId(user)))
                throw new AgentCostConnectionException("identityMismatch");
            var properties = new AuthenticationProperties { RedirectUri = ReturnRoute };
            properties.Items[ObjectIdProperty] = ObjectId(user);
            properties.Items[TenantProperty] = TenantId(user);
            properties.Items[ExpiresProperty] = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString();
            return properties;
        }

        // Created only by the same-origin POST; a foreign page cannot start a connection by linking to MVC.
        public static string CreateIntent(ClaimsPrincipal user, IDataProtectionProvider protection) =>
            protection.CreateProtector(TicketPurpose).Protect(JsonConvert.SerializeObject(PropertiesFor(user).Items));

        public static AuthenticationProperties ReadIntent(string intent, ClaimsPrincipal user, IDataProtectionProvider protection)
        {
            try
            {
                var json = protection.CreateProtector(TicketPurpose).Unprotect(intent ?? "");
                var dictionary = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(
                    json);
                var properties = new AuthenticationProperties(dictionary) { RedirectUri = ReturnRoute };
                return IsBound(properties, user) ? properties : null;
            }
            catch (Exception) { return null; }
        }

        public static bool IsBound(AuthenticationProperties properties, ClaimsPrincipal user)
        {
            return properties?.Items != null
                && properties.Items.TryGetValue(ObjectIdProperty, out var oid)
                && properties.Items.TryGetValue(TenantProperty, out var tenant)
                && properties.Items.TryGetValue(ExpiresProperty, out var expiry)
                && long.TryParse(expiry, out var seconds)
                && seconds >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && string.Equals(oid, ObjectId(user), StringComparison.OrdinalIgnoreCase)
                && string.Equals(tenant, TenantId(user), StringComparison.OrdinalIgnoreCase)
                && PortalAccessPolicy.FromAppSettings().Evaluate(user).Administration;
        }

        public static void ConfigureOptions(OpenIdConnectOptions options, AppConfig config,
            Func<ClaimsPrincipal, AuthenticationProperties, string, Task> connect = null)
        {
            options.Authority = config.Authority.TrimEnd('/') + "/v2.0";
            options.ClientId = config.ClientID;
            options.CallbackPath = new PathString(AgentCostDelegatedTokenProvider.CallbackPath);
            // Validate the identity token before handing the code to MSAL, which owns redemption and the
            // durable token cache. Its authorization-code API does not take the handler's PKCE verifier.
            options.ResponseType = OpenIdConnectResponseType.CodeIdToken;
            options.UsePkce = false;
            options.SaveTokens = false;
            options.TokenValidationParameters.ValidateIssuer = true;
            options.Scope.Clear();
            foreach (var scope in ("openid profile offline_access " + AgentCostDelegatedTokenProvider.Scope).Split(' '))
                options.Scope.Add(scope);
            options.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri = AgentCostDelegatedTokenProvider.RedirectUri(config);
                        context.ProtocolMessage.LoginHint = DelegatedGraphConsent.LoginHintFor(context.HttpContext.User);
                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived = async context =>
                    {
                        var outcome = "connected";
                        try
                        {
                            var principal = context.Principal;
                            if (!IsBound(context.Properties, principal)
                                || !string.Equals(TenantId(principal), config.TenantGUID.ToString(), StringComparison.OrdinalIgnoreCase))
                                throw new AgentCostConnectionException("identityMismatch");
                            await (connect ?? ((user, properties, code) => ConnectAsync(config, properties, code)))(
                                principal, context.Properties, context.ProtocolMessage.Code);
                        }
                        catch (AgentCostConnectionException ex) { outcome = SafeOutcome(ex.Code); }
                        catch (Exception) { outcome = "failed"; }
                        // No token is placed in the cookie, and this optional connection cannot replace portal sign-in.
                        context.HandleResponse();
                        context.Response.Redirect(ReturnRoute + "?connection=" + outcome);
                    },
                    OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        context.Response.Redirect(ReturnRoute + "?connection=failed");
                        return Task.CompletedTask;
                    },
                };
        }

        private static async Task ConnectAsync(AppConfig config, AuthenticationProperties properties, string code)
        {
            var store = AgentCostConnectionStore.TryOpen(config);
            if (store == null) throw new AgentCostConnectionException("storageNotConfigured");
            await AgentCostDelegatedTokenProvider.Create(config, store).ConnectAsync(code,
                AgentCostDelegatedTokenProvider.RedirectUri(config),
                properties.Items[TenantProperty], properties.Items[ObjectIdProperty],
                VerifyAccessAsync);
        }

        // Replaceable so tests can observe the probe without a network call.
        internal static async Task VerifyAccessAsync(string token)
        {
            // The per-user route: it is the first read every import makes, and the one the delegated token is
            // known to be accepted on (the tenant-wide per-agent route is refused to every client but Microsoft's).
            var date = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using (var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.powerplatform.com/licensing/entitlements/MCSMessages/users"
                + "?api-version=2024-10-01&pageSize=1&fromDate=" + date + "&toDate=" + date))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var response = await VerificationClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                        throw new AgentCostConnectionException("accessDenied");
                    if (!response.IsSuccessStatusCode) throw new AgentCostConnectionException("failed");
                }
            }
        }

        private static string SafeOutcome(string code) =>
            code == "storageNotConfigured" || code == "consentOrPolicy" || code == "identityMismatch"
                || code == "accessDenied" ? code : "failed";
    }
}

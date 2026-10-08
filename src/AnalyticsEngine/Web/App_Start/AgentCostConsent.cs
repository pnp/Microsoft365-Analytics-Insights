using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.OpenIdConnect;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Web.Security;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb
{
    /// <summary>A separate, passive v2 OIDC connection: ordinary sign-in and Teams consent are unchanged.</summary>
    public static class AgentCostConsent
    {
        public const string AuthenticationType = "AgentCostConnection";
        public const string ReturnRoute = "/#/admin/agent-cost-connection";
        public const string ObjectIdProperty = "billing.oid";
        public const string TenantProperty = "billing.tid";
        public const string ExpiresProperty = "billing.expires";
        private const string TicketPurpose = "AgentCostConnectIntent.v1";
        private static readonly HttpClient VerificationClient = new HttpClient();

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
            properties.Dictionary[ObjectIdProperty] = ObjectId(user);
            properties.Dictionary[TenantProperty] = TenantId(user);
            properties.Dictionary[ExpiresProperty] = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString();
            return properties;
        }

        // Created only by the same-origin POST; a foreign page cannot start a connection by linking to MVC.
        public static string CreateIntent(ClaimsPrincipal user) => Convert.ToBase64String(MachineKey.Protect(
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(PropertiesFor(user).Dictionary)), TicketPurpose));

        public static AuthenticationProperties ReadIntent(string intent, ClaimsPrincipal user)
        {
            try
            {
                var bytes = MachineKey.Unprotect(Convert.FromBase64String(intent ?? ""), TicketPurpose);
                var dictionary = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(
                    Encoding.UTF8.GetString(bytes));
                var properties = new AuthenticationProperties(dictionary) { RedirectUri = ReturnRoute };
                return IsBound(properties, user) ? properties : null;
            }
            catch (Exception) { return null; }
        }

        public static bool IsBound(AuthenticationProperties properties, ClaimsPrincipal user)
        {
            return properties?.Dictionary != null
                && properties.Dictionary.TryGetValue(ObjectIdProperty, out var oid)
                && properties.Dictionary.TryGetValue(TenantProperty, out var tenant)
                && properties.Dictionary.TryGetValue(ExpiresProperty, out var expiry)
                && long.TryParse(expiry, out var seconds)
                && seconds >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && string.Equals(oid, ObjectId(user), StringComparison.OrdinalIgnoreCase)
                && string.Equals(tenant, TenantId(user), StringComparison.OrdinalIgnoreCase)
                && PortalAccessPolicy.FromAppSettings().Evaluate(user).Administration;
        }

        public static OpenIdConnectAuthenticationOptions CreateOptions(AppConfig config,
            Func<AuthenticationTicket, string, Task> connect = null)
        {
            return new OpenIdConnectAuthenticationOptions
            {
                AuthenticationType = AuthenticationType,
                AuthenticationMode = AuthenticationMode.Passive,
                Authority = config.Authority.TrimEnd('/') + "/v2.0",
                ClientId = config.ClientID,
                RedirectUri = AgentCostDelegatedTokenProvider.RedirectUri(config),
                CallbackPath = new PathString(AgentCostDelegatedTokenProvider.CallbackPath),
                ResponseType = "code id_token",
                Scope = "openid profile offline_access " + AgentCostDelegatedTokenProvider.Scope,
                TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters { ValidateIssuer = true },
                Notifications = new OpenIdConnectAuthenticationNotifications
                {
                    RedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.LoginHint = DelegatedGraphConsent.LoginHintFor(context.OwinContext.Authentication.User);
                        return Task.CompletedTask;
                    },
                    AuthorizationCodeReceived = async context =>
                    {
                        var outcome = "connected";
                        try
                        {
                            var principal = new ClaimsPrincipal(context.AuthenticationTicket.Identity);
                            if (!IsBound(context.AuthenticationTicket.Properties, principal)
                                || !string.Equals(TenantId(principal), config.TenantGUID.ToString(), StringComparison.OrdinalIgnoreCase))
                                throw new AgentCostConnectionException("identityMismatch");
                            await (connect ?? ((ticket, code) => ConnectAsync(config, ticket, code)))(
                                context.AuthenticationTicket, context.Code);
                        }
                        catch (AgentCostConnectionException ex) { outcome = SafeOutcome(ex.Code); }
                        catch (Exception) { outcome = "failed"; }
                        // No token is placed in the cookie, and this optional connection cannot replace portal sign-in.
                        context.HandleResponse();
                        context.Response.Redirect(ReturnRoute + "?connection=" + outcome);
                    },
                    AuthenticationFailed = context =>
                    {
                        context.HandleResponse();
                        context.Response.Redirect(ReturnRoute + "?connection=failed");
                        return Task.CompletedTask;
                    },
                },
            };
        }

        private static async Task ConnectAsync(AppConfig config, AuthenticationTicket ticket, string code)
        {
            var store = AgentCostConnectionStore.TryOpen(config);
            if (store == null) throw new AgentCostConnectionException("storageNotConfigured");
            await AgentCostDelegatedTokenProvider.Create(config, store).ConnectAsync(code,
                AgentCostDelegatedTokenProvider.RedirectUri(config),
                ticket.Properties.Dictionary[TenantProperty], ticket.Properties.Dictionary[ObjectIdProperty],
                VerifyAccessAsync);
        }

        private static async Task VerifyAccessAsync(string token)
        {
            var date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using (var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.powerplatform.com/licensing/entitlements/MCSMessages/resources"
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

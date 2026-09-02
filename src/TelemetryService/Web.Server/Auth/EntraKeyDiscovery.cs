using System;

namespace Web.Auth
{
    /// <summary>
    /// Builds the OpenID Connect metadata address this API uses to fetch Entra's token-signing keys.
    ///
    /// The only difference from the address Microsoft.Identity.Web would derive on its own is the
    /// <c>appid</c> query parameter, and it exists purely so Entra can tell who is asking.
    ///
    /// Without it the API fetches signing keys anonymously:
    ///     https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration
    ///     https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys
    /// Those requests carry nothing that identifies the application, so eSTS cannot attribute the
    /// key discovery to this app registration and the S360 MISE Compliance KPI sees no key-discovery
    /// telemetry for it at all. Neither Microsoft.Identity.Web nor Microsoft.IdentityModel adds the
    /// parameter for us, so it has to be set here.
    ///
    /// Adding <c>?appid={clientId}</c> to the metadata address is enough to fix both requests: eSTS
    /// echoes the parameter into the <c>jwks_uri</c> it returns, so the follow-up key fetch is
    /// attributed too, and it also narrows the response to the keys that actually apply to this app.
    ///
    /// This does NOT make the service MISE-compliant - MISE is a separate stack that cannot be
    /// referenced from this public repository (its packages are on an internal feed only, and CI
    /// restores from nuget.org). It only stops the app being invisible to key-discovery telemetry.
    /// </summary>
    public static class EntraKeyDiscovery
    {
        /// <summary>Query parameter eSTS uses to attribute a key-discovery request to an application.</summary>
        public const string AppIdParameterName = "appid";

        /// <summary>
        /// Builds the v2.0 OpenID Connect metadata address for <paramref name="tenantId"/>, tagged with
        /// <paramref name="clientId"/>.
        /// </summary>
        /// <returns>
        /// The metadata address, or <c>null</c> when any part is missing - in which case the caller
        /// should leave <c>JwtBearerOptions.MetadataAddress</c> alone and let Microsoft.Identity.Web
        /// derive it. Local development and the integration tests run without these settings.
        /// </returns>
        public static string? BuildMetadataAddress(string? instance, string? tenantId, string? clientId)
        {
            if (string.IsNullOrWhiteSpace(instance) || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
            {
                return null;
            }

            var authority = $"{instance.Trim().TrimEnd('/')}/{Uri.EscapeDataString(tenantId.Trim())}";

            // v2.0 is not optional: Microsoft.Identity.Web forces the authority to v2 (EnsureAuthorityIsV2),
            // so a v1 metadata address here would validate tokens against a different issuer than expected.
            return $"{authority}/v2.0/.well-known/openid-configuration" +
                   $"?{AppIdParameterName}={Uri.EscapeDataString(clientId.Trim())}";
        }
    }
}

namespace Web.Auth
{
    public static class DashboardAuthorization
    {
        /// <summary>Entra app role a caller must be assigned to read the dashboard.</summary>
        public const string RequiredRole = "Telemetry.Dashboard.Read";

        /// <summary>
        /// Delegated scope the SPA requests and the API demands. Must match the scope exposed by the
        /// app registration and the <c>AzureAd:Scopes</c> setting returned by the client bootstrap.
        /// </summary>
        public const string RequiredScope = "Telemetry.Read";

        /// <summary>
        /// Policy demanding <see cref="RequiredRole"/> AND <see cref="RequiredScope"/>.
        ///
        /// Both must live in one policy rather than being expressed as <c>[Authorize(Roles = ...)]</c>
        /// plus <c>[RequiredScope(...)]</c>. That combination looks correct but silently enforces
        /// only the role: <c>RequiredScopeAttribute</c> is nothing but endpoint metadata, and the
        /// handler that reads it is only reached through the default authorization policy - which
        /// <c>[Authorize(Roles = ...)]</c> replaces rather than extends.
        /// </summary>
        public const string PolicyName = "DashboardRead";
    }
}

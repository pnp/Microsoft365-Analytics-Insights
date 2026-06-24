namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Claim types used to carry the signed-in admin's Microsoft Graph token in the encrypted
    /// auth cookie. This lets the SPA obtain a Graph token (via SiteTokenAPI) without Redis.
    /// </summary>
    public static class GraphTokenClaims
    {
        /// <summary>The OAuth refresh token, captured during the OIDC sign-in redirect.</summary>
        public const string RefreshToken = "urn:aa:graph_refresh_token";
    }
}

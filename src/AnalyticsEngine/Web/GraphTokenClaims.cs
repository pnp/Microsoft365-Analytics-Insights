namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Claim types used to carry the signed-in admin's Microsoft Graph token in the encrypted
    /// auth cookie. This lets the SPA obtain a Graph token (via SiteTokenAPI) without any server-side token store.
    /// </summary>
    public static class GraphTokenClaims
    {
        /// <summary>
        /// The OAuth refresh token, captured when the admin connects Microsoft Teams on the Teams permissions page
        /// (<c>AccountController.ConnectTeams</c>). A plain sign-in doesn't capture one (issue #670).
        /// </summary>
        public const string RefreshToken = "urn:aa:graph_refresh_token";
    }
}

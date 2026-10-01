using Common.Entities.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// When the portal asks Entra ID for the signed-in admin's own delegated Microsoft Graph token, and what it
    /// does when Entra ID says no (issue #670).
    /// </summary>
    /// <remarks>
    /// <para>
    /// That token is only used by Teams deep analytics. The Teams permissions page lists the admin's Teams
    /// through Graph (<c>api/SiteTokenAPI</c>), and <c>TeamsAuthAPIController.Put</c> hands the refresh token to
    /// the importer for each Team it authorises. Nothing else in the portal needs it.
    /// </para>
    /// <para>
    /// Every sign-in used to request it. A tenant that had not granted
    /// the delegated <c>Team.ReadBasic.All</c> / <c>ChannelMessage.Read.All</c> permissions - usually because it
    /// does not use Teams deep analytics at all - got <c>AADSTS65001</c> from the token endpoint. The exception
    /// escaped the OIDC middleware, and nobody could open any page.
    /// </para>
    /// <para>
    /// A sign-in now asks for <see cref="SignInScopes"/> only. The Teams scopes are requested only
    /// when an admin selects <em>Connect to Microsoft Teams</em> on the Teams permissions page
    /// (<c>AccountController.ConnectTeams</c>), which re-runs the OIDC challenge marked with
    /// <see cref="RequestPropertyKey"/>. ASP.NET Core protects the challenge's
    /// <see cref="AuthenticationProperties"/> in the OIDC <c>state</c>, so the marker comes back tamper-proof on
    /// the callback. A failure at either end of that round trip sends the admin back to the Teams permissions
    /// page with an outcome key, and never throws.
    /// </para>
    /// </remarks>
    public static class DelegatedGraphConsent
    {
        /// <summary>
        /// What every sign-in asks for: OpenID Connect's own scopes, and nothing that needs consent beyond
        /// signing in.
        /// </summary>
        public const string SignInScopes = "openid email profile";

        /// <summary>The delegated Graph permissions Teams deep analytics needs.</summary>
        public const string TeamsGraphScopes =
            "https://graph.microsoft.com/Team.ReadBasic.All https://graph.microsoft.com/ChannelMessage.Read.All";

        /// <summary>
        /// Scopes for the Teams connection and its code redemption. <c>offline_access</c> is what gets a refresh
        /// token back, which the importer keeps for each authorised Team.
        /// </summary>
        public const string TeamsConnectScopes = SignInScopes + " offline_access " + TeamsGraphScopes;

        /// <summary>
        /// Key in the challenge's <see cref="AuthenticationProperties"/> that marks it as a Teams connection rather
        /// than a sign-in.
        /// </summary>
        internal const string RequestPropertyKey = "aa.delegatedGraph";

        internal const string TeamsRequest = "teams";

        /// <summary>Short-lived handoff between the token-response and token-validated OIDC events.</summary>
        internal const string RefreshTokenPropertyKey = "aa.delegatedGraph.refreshToken";

        /// <summary>The SPA route the Teams connection returns to: Administration &gt; Teams permissions.</summary>
        public const string TeamsPermissionsRoute = "/#/admin/teams-permissions";

        /// <summary>
        /// Query parameters on <see cref="TeamsPermissionsRoute"/> after a failed connection. The Teams
        /// permissions page reads them (<c>src/auth/teamsConnect.ts</c>).
        /// </summary>
        public const string OutcomeParameter = "teamsConnect";

        public const string ErrorCodeParameter = "teamsConnectError";

        // Outcome keys. Stable identifiers, not text: the SPA maps each one to a translated sentence, and
        // src/i18n/lint/serverAuthoredText.test.ts reads these constants to keep the two sides in step.

        /// <summary>The permissions are not granted to the runtime app. Trying again won't help until an admin grants them.</summary>
        public const string OutcomeConsentRequired = "consent_required";

        /// <summary>The request was cancelled or declined at Entra ID.</summary>
        public const string OutcomeAccessDenied = "access_denied";

        /// <summary>Anything else. The details are in Application Insights.</summary>
        public const string OutcomeFailed = "failed";

        /// <summary>
        /// Entra ID errors meaning "these permissions aren't granted to this app". An admin has to add them to
        /// the runtime app registration, or consent to them, before another attempt can succeed.
        /// </summary>
        private static readonly HashSet<int> ConsentErrorCodes = new HashSet<int>
        {
            65001,  // The user or administrator has not consented to use the application.
            65005,  // Misconfigured application: the scope isn't in the app registration's required permissions.
            90094,  // The grant requires admin permission.
            650056, // Misconfigured application: permissions not listed, or not consented, for the resource.
            650057, // Invalid resource: not listed in the app registration's requested permissions.
        };

        /// <summary>The user declined the consent prompt.</summary>
        private static readonly HashSet<int> DeclinedErrorCodes = new HashSet<int>
        {
            65004,
        };

        private static readonly Regex AadstsCode = new Regex(@"\bAADSTS(\d{4,7})\b", RegexOptions.CultureInvariant);

        /// <summary>What may be echoed into the return URL, and from there shown on the page.</summary>
        private static readonly Regex DisplayableErrorCode = new Regex("^[A-Za-z0-9_]{1,40}$", RegexOptions.CultureInvariant);

        /// <summary>Properties for the Teams connection challenge.</summary>
        public static AuthenticationProperties CreateTeamsConnectProperties()
        {
            // The return address is fixed rather than taken from the request, so the endpoint that issues this
            // challenge cannot be used as an open redirect.
            var properties = new AuthenticationProperties { RedirectUri = TeamsPermissionsRoute };
            properties.Items[RequestPropertyKey] = TeamsRequest;
            return properties;
        }

        /// <summary>True when these properties belong to a Teams connection rather than a sign-in.</summary>
        public static bool IsTeamsConnect(AuthenticationProperties properties)
        {
            return properties?.Items != null
                && properties.Items.TryGetValue(RequestPropertyKey, out var value)
                && string.Equals(value, TeamsRequest, StringComparison.Ordinal);
        }

        /// <summary>The <c>scope</c> an authentication request for this challenge should carry.</summary>
        public static string ScopesFor(AuthenticationProperties challengeProperties)
        {
            return IsTeamsConnect(challengeProperties) ? TeamsConnectScopes : SignInScopes;
        }

        /// <summary>Applies the marked challenge's scope and account hint to the outgoing OIDC request.</summary>
        public static void ConfigureChallenge(
            AuthenticationProperties properties,
            OpenIdConnectMessage message,
            ClaimsPrincipal user)
        {
            if (!IsTeamsConnect(properties) || message == null)
            {
                return;
            }

            message.Scope = TeamsConnectScopes;
            message.LoginHint = LoginHintFor(user);
        }

        /// <summary>Holds the Teams refresh token until the matching principal has been validated.</summary>
        public static void CaptureRefreshToken(
            AuthenticationProperties properties,
            OpenIdConnectMessage tokenResponse)
        {
            if (IsTeamsConnect(properties) && !string.IsNullOrEmpty(tokenResponse?.RefreshToken))
            {
                properties.Items[RefreshTokenPropertyKey] = tokenResponse.RefreshToken;
            }
        }

        /// <summary>Adds the captured refresh token to the encrypted cookie principal.</summary>
        public static void CompleteTokenValidation(
            AuthenticationProperties properties,
            ClaimsPrincipal principal)
        {
            if (!IsTeamsConnect(properties)
                || properties?.Items == null
                || !properties.Items.TryGetValue(RefreshTokenPropertyKey, out var refreshToken)
                || string.IsNullOrEmpty(refreshToken)
                || !(principal?.Identity is ClaimsIdentity identity))
            {
                return;
            }

            identity.AddClaim(new Claim(GraphTokenClaims.RefreshToken, refreshToken));
            properties.Items.Remove(RefreshTokenPropertyKey);
        }

        /// <summary>
        /// The signed-in user's sign-in name, so the Teams connection reuses their Entra ID session instead of
        /// showing an account picker when the browser holds several accounts. <c>null</c> when there isn't one.
        /// </summary>
        public static string LoginHintFor(ClaimsPrincipal user)
        {
            var identity = user?.Identity as ClaimsIdentity;
            if (identity == null || !identity.IsAuthenticated)
            {
                return null;
            }

            // v1 tokens carry "upn", which the JWT handler maps to ClaimTypes.Upn; v2 tokens carry
            // "preferred_username". A guest has neither, and simply gets Entra ID's normal account selection.
            var hint = identity.FindFirst(ClaimTypes.Upn)?.Value ?? identity.FindFirst("preferred_username")?.Value;
            return string.IsNullOrWhiteSpace(hint) ? null : hint;
        }

        /// <summary>
        /// Where to send the admin after a failed connection: back to the Teams permissions page, carrying the
        /// outcome key and, when there is one, Entra ID's error code.
        /// </summary>
        public static string FailureReturnUri(ConsentFailure failure)
        {
            var outcome = failure?.Outcome;
            if (outcome != OutcomeConsentRequired && outcome != OutcomeAccessDenied)
            {
                outcome = OutcomeFailed;
            }

            var uri = $"{TeamsPermissionsRoute}?{OutcomeParameter}={outcome}";

            // Only a plain identifier is echoed: it lands in the address bar and on the page.
            var errorCode = failure?.ErrorCode;
            if (!string.IsNullOrEmpty(errorCode) && DisplayableErrorCode.IsMatch(errorCode))
            {
                uri += $"&{ErrorCodeParameter}={errorCode}";
            }

            return uri;
        }

        /// <summary>Describes a failure to redeem the authorisation code at the token endpoint.</summary>
        public static ConsentFailure DescribeTokenFailure(Exception exception)
        {
            if (exception is OAuthTokenRequestException oauth)
            {
                var codes = oauth.ErrorCodes.Count > 0
                    ? oauth.ErrorCodes.ToList()
                    : ErrorCodesIn(oauth.ErrorDescription).ToList();

                return new ConsentFailure(Classify(oauth.Error, oauth.SubError, codes), DisplayCode(codes, oauth.Error));
            }

            // A network failure, a timeout, an unreadable response: nothing an admin can act on from the page.
            return new ConsentFailure(OutcomeFailed, null);
        }

        /// <summary>
        /// Describes a failed callback: Entra ID answered the authorisation request with an error, or the
        /// response could not be validated.
        /// </summary>
        public static ConsentFailure DescribeAuthorizationFailure(OpenIdConnectMessage response, Exception exception)
        {
            var error = response?.Error;
            var description = response?.ErrorDescription;

            // Katana copies both onto the exception it raises for an error response.
            if (string.IsNullOrEmpty(error)) error = exception?.Data["error"] as string;
            if (string.IsNullOrEmpty(description)) description = exception?.Data["error_description"] as string;
            if (string.IsNullOrEmpty(description)) description = exception?.Message;

            var codes = ErrorCodesIn(description).ToList();
            if (string.IsNullOrEmpty(error) && codes.Count == 0)
            {
                // Not an Entra ID error at all, e.g. the ID token failed validation.
                return new ConsentFailure(OutcomeFailed, null);
            }

            return new ConsentFailure(Classify(error, null, codes), DisplayCode(codes, error));
        }

        /// <summary>Maps an OAuth error to an outcome key.</summary>
        internal static string Classify(string error, string subError, IEnumerable<int> errorCodes)
        {
            var codes = errorCodes?.ToList() ?? new List<int>();

            // Consent first: "Need admin approval" followed by "return to the application" is still a missing
            // grant, and telling the admin they cancelled would send them round the same loop.
            if (codes.Any(ConsentErrorCodes.Contains)
                || string.Equals(subError, "consent_required", StringComparison.OrdinalIgnoreCase)
                || string.Equals(error, "consent_required", StringComparison.OrdinalIgnoreCase))
            {
                return OutcomeConsentRequired;
            }

            if (codes.Any(DeclinedErrorCodes.Contains)
                || string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase))
            {
                return OutcomeAccessDenied;
            }

            return OutcomeFailed;
        }

        internal static IEnumerable<int> ErrorCodesIn(string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                yield break;
            }

            foreach (Match match in AadstsCode.Matches(description))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                {
                    yield return code;
                }
            }
        }

        private static string DisplayCode(IList<int> codes, string error)
        {
            if (codes.Count > 0)
            {
                return "AADSTS" + codes[0].ToString(CultureInfo.InvariantCulture);
            }

            return string.IsNullOrEmpty(error) ? null : error;
        }
    }

    /// <summary>Why a Teams connection failed: an outcome key, and Entra ID's error code when it gave one.</summary>
    public sealed class ConsentFailure
    {
        public ConsentFailure(string outcome, string errorCode)
        {
            Outcome = outcome;
            ErrorCode = errorCode;
        }

        public string Outcome { get; }

        /// <summary>For example <c>AADSTS65001</c>, or the OAuth <c>error</c> when there's no AADSTS code.</summary>
        public string ErrorCode { get; }
    }
}

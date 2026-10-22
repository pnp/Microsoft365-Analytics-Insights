using Common.Entities.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Common.Entities.Models
{
    /// <summary>
    /// OAuth token model. Holds things MSAL doesn't support (handling of refresh tokens)
    /// </summary>
    public abstract class AuthToken
    {
        public AuthToken() { }

        public abstract string AccessToken { get; set; }


        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    public class JSonToken : AuthToken
    {
        public JSonToken(RefreshOAuthToken auth)
        {
            this.AccessToken = auth.AccessToken;
        }

        [JsonProperty("accessToken")]
        public override string AccessToken { get; set; }
        public int MyProperty { get; set; }
    }

    public class RefreshOAuthToken : AuthToken
    {
        // One HttpClient - one connection pool - for every token request this process makes. A new, undisposed
        // HttpClient per request leaks a socket each time.
        private static readonly HttpClient TokenEndpointClient = new HttpClient();

        [JsonProperty("access_token")]
        public override string AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }

        /// <summary>
        /// How many seconds the access token lasts from when it was issued (Entra ID's <c>expires_in</c>: 60 to 90 minutes
        /// for Microsoft Graph); <c>null</c> when the response didn't say.
        /// </summary>
        [JsonProperty("expires_in")]
        public int? ExpiresIn { get; set; }

        public static async Task<RefreshOAuthToken> GetAccessToken(string code, string scopes, AppConfig azureADConfig)
        {

            // https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-auth-code-flow#request-an-access-token
            var loginData = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("redirect_uri", azureADConfig.WebAppURL),
                new KeyValuePair<string, string>("client_id", azureADConfig.ClientID),
                new KeyValuePair<string, string>("client_secret", azureADConfig.ClientSecret),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("scope", scopes),           // Should include offline_access
                new KeyValuePair<string, string>("grant_type", "authorization_code")
            };

            // V2 endpoint
            var authResponse = await TokenEndpointClient.PostAsync($"{azureADConfig.Authority}/oauth2/v2.0/token", new FormUrlEncodedContent(loginData));
            var responseBody = await authResponse.Content.ReadAsStringAsync();
            try
            {
                authResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                throw new OAuthTokenRequestException(
                    $"Got error '{ex.Message}' trying to get OAuth token from Azure AD.\nResponse body: '{responseBody}'",
                    authResponse.StatusCode, responseBody, ex);
            }

            return JsonConvert.DeserializeObject<RefreshOAuthToken>(responseBody);
        }

        /// <summary>
        /// Redeems a refresh token for a new access token (and, normally, a new refresh token).
        /// </summary>
        /// <exception cref="RefreshTokenRequestException">Entra ID refused the request. It carries the OAuth error, so a
        /// caller can tell a dead refresh token (<c>invalid_grant</c>) from anything else.</exception>
        /// <exception cref="HttpRequestException">Entra ID couldn't be reached.</exception>
        public static Task<RefreshOAuthToken> GetNewRefreshToken(string refreshToken, AppConfig azureADConfig)
            => GetNewRefreshToken(refreshToken, azureADConfig, TokenEndpointClient);

        internal static async Task<RefreshOAuthToken> GetNewRefreshToken(string refreshToken, AppConfig azureADConfig, HttpClient httpClient)
        {

            // https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-auth-code-flow#request-an-access-token
            var loginData = new List<KeyValuePair<string, string>>();
            loginData.Add(new KeyValuePair<string, string>("redirect_uri", azureADConfig.WebAppURL));
            loginData.Add(new KeyValuePair<string, string>("client_id", azureADConfig.ClientID));
            loginData.Add(new KeyValuePair<string, string>("client_secret", azureADConfig.ClientSecret));
            loginData.Add(new KeyValuePair<string, string>("refresh_token", refreshToken));
            loginData.Add(new KeyValuePair<string, string>("grant_type", "refresh_token"));



            var authResponse = await httpClient.PostAsync($"{azureADConfig.Authority}/oauth2/v2.0/token", new FormUrlEncodedContent(loginData));
            var responseBody = await authResponse.Content.ReadAsStringAsync();

            try
            {
                authResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                var refused = new RefreshTokenRequestException(ex.Message, authResponse.StatusCode, responseBody, ex);

                // The error fields, not the body: a refusal carries no token, but its description can name the user.
                Console.WriteLine($"Got HTTP exception renewing token: {ex.Message}. OAuth error: '{refused.Error}', suberror: '{refused.SubError}', " +
                    $"error codes: [{string.Join(",", refused.ErrorCodes)}].");
                throw refused;
            }

            return JsonConvert.DeserializeObject<RefreshOAuthToken>(responseBody);
        }
    }

    /// <summary>
    /// Entra ID refused to redeem a refresh token.
    /// </summary>
    /// <remarks>
    /// Still an <see cref="HttpRequestException"/> with the same message as before, so existing catch blocks - such as the
    /// portal's site-token API - and anyone searching the logs see no difference. It adds the OAuth error, so the importer
    /// deletes a Team's stored token only when Entra ID says the token itself is dead (<c>invalid_grant</c>), not when the
    /// app's own credential has expired (<c>invalid_client</c>), Entra ID is throttling or Entra ID is down.
    /// </remarks>
    public class RefreshTokenRequestException : HttpRequestException
    {
        public RefreshTokenRequestException(string message, HttpStatusCode statusCode, string responseBody, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;

            var body = OAuthErrorBody.Parse(responseBody);
            Error = body.Error;
            SubError = body.SubError;
            ErrorCodes = body.ErrorCodes;
        }

        public HttpStatusCode StatusCode { get; }

        /// <summary>The OAuth <c>error</c>, e.g. <c>invalid_grant</c>; <c>null</c> when the body wasn't Entra ID's JSON.</summary>
        public string Error { get; }

        /// <summary>Entra ID's <c>suberror</c>, e.g. <c>bad_token</c>.</summary>
        public string SubError { get; }

        /// <summary>Entra ID's <c>error_codes</c>: <c>700082</c> is <c>AADSTS700082</c>.</summary>
        public IReadOnlyList<int> ErrorCodes { get; }
    }

    /// <summary>
    /// Entra ID refused an OAuth token request.
    /// </summary>
    /// <remarks>
    /// Carries the OAuth error fields from the response body, so a caller can tell "an administrator hasn't
    /// consented to these scopes" (<c>AADSTS65001</c>) apart from a transient failure without parsing the
    /// message. It is still an <see cref="ApplicationException"/> with the same message as before, so existing
    /// catch blocks, and anyone searching the logs for that message, see no difference.
    /// </remarks>
    public class OAuthTokenRequestException : ApplicationException
    {
        public OAuthTokenRequestException(string message, HttpStatusCode statusCode, string responseBody, Exception innerException)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;

            var body = OAuthErrorBody.Parse(responseBody);
            Error = body.Error;
            SubError = body.SubError;
            ErrorDescription = body.ErrorDescription;
            ErrorCodes = body.ErrorCodes;
        }

        public HttpStatusCode StatusCode { get; }

        public string ResponseBody { get; }

        /// <summary>The OAuth <c>error</c>, e.g. <c>invalid_grant</c>.</summary>
        public string Error { get; }

        /// <summary>Entra ID's <c>suberror</c>, e.g. <c>consent_required</c>.</summary>
        public string SubError { get; }

        public string ErrorDescription { get; }

        /// <summary>Entra ID's <c>error_codes</c>: <c>65001</c> is <c>AADSTS65001</c>.</summary>
        public IReadOnlyList<int> ErrorCodes { get; } = new int[0];
    }

    /// <summary>
    /// The OAuth error fields of an Entra ID token-endpoint refusal, read on a best-effort basis.
    /// </summary>
    internal sealed class OAuthErrorBody
    {
        public string Error { get; private set; }

        public string SubError { get; private set; }

        public string ErrorDescription { get; private set; }

        public IReadOnlyList<int> ErrorCodes { get; private set; } = new int[0];

        public static OAuthErrorBody Parse(string responseBody)
        {
            var result = new OAuthErrorBody();
            var body = TryParse(responseBody);
            if (body == null)
            {
                return result;
            }

            // Best effort: this runs while reporting a failure, so an unexpected shape must never replace the
            // real error with a parsing one.
            result.Error = StringField(body, "error");
            result.SubError = StringField(body, "suberror");
            result.ErrorDescription = StringField(body, "error_description");

            if (body["error_codes"] is JArray codes)
            {
                // Json.NET holds an integer as a long, or a BigInteger when it's too big for one. Anything else - a
                // string, a float, a number out of range - is simply not an error code.
                result.ErrorCodes = codes
                    .OfType<JValue>()
                    .Select(code => code.Value)
                    .OfType<long>()
                    .Where(code => code >= int.MinValue && code <= int.MaxValue)
                    .Select(code => (int)code)
                    .ToList();
            }

            return result;
        }

        private static string StringField(JObject body, string name)
        {
            var value = body[name];
            return value != null && value.Type == JTokenType.String ? (string)value : null;
        }

        private static JObject TryParse(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            try
            {
                // A gateway or proxy in the way can answer with HTML rather than Entra ID's JSON.
                return JToken.Parse(responseBody) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

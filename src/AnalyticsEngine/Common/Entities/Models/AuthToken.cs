using Common.Entities.Config;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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

        [JsonProperty("access_token")]
        public override string AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }

        public static async Task<RefreshOAuthToken> GetAccessToken(string code, string scopes, AppConfig azureADConfig)
        {

            // https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-auth-code-flow#request-an-access-token
            HttpClient httpClient = new HttpClient();
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
            var authResponse = await httpClient.PostAsync($"{azureADConfig.Authority}/oauth2/v2.0/token", new FormUrlEncodedContent(loginData));
            var responseBody = await authResponse.Content.ReadAsStringAsync();
            try
            {
                authResponse.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                throw new ApplicationException($"Got error '{ex.Message}' trying to get OAuth token from Azure AD.\nResponse body: '{responseBody}'");
            }

            return JsonConvert.DeserializeObject<RefreshOAuthToken>(responseBody);
        }

        public static async Task<RefreshOAuthToken> GetNewRefreshToken(string refreshToken, AppConfig azureADConfig)
        {

            // https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-auth-code-flow#request-an-access-token
            var httpClient = new HttpClient();
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
                Console.WriteLine($"Got HTTP exception renewing token: {ex.Message}. Response body: {responseBody}");
                throw;
            }

            return JsonConvert.DeserializeObject<RefreshOAuthToken>(responseBody);
        }
    }

}

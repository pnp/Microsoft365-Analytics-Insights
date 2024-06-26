using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace Common.DataUtils
{
    public static class StringUtils
    {
        /// <summary>
        /// Hack for https://github.com/dotnet/runtime/issues/21626
        /// </summary>
        public static bool IsValidAbsoluteUrl(string url)
        {
            try
            {
                new Uri(url);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (UriFormatException)
            {
                return false;
            }
            return true;
        }

        public static string GetOnlineMeetingId(string copilotDocContextId, string userGuid)
        {
            var meetingIdFragment = StringUtils.GetMeetingIdFragmentFromMeetingThreadUrl(copilotDocContextId);
            if (meetingIdFragment == null)
            {
                throw new Exception($"Could not parse meeting id from url {copilotDocContextId}");
            }

            // Get/create meeting in DB
            var meetingId = $"{userGuid}_{meetingIdFragment}";
            return meetingId;
        }

        public static string GetDriveItemId(string copilotDocContextId)
        {
            var uri = new Uri(copilotDocContextId);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var sourcedoc = query["sourcedoc"];
            if (string.IsNullOrEmpty(sourcedoc))
            {
                return null;
            }
            return sourcedoc.Replace("{", "").Replace("}", "");
        }
        public static string GetSiteUrl(string copilotDocContextId)
        {
            if (!StringUtils.IsValidAbsoluteUrl(copilotDocContextId))
            {
                return null;
            }
            var uri = new Uri(copilotDocContextId);

            const string SEP = "/";

            // Remove SharePoint app pages (layouts)
            var absoluteUriMinusQueryAndAppPages = uri.AbsolutePath.Replace("/_layouts/15", "");

            var urlSegments = absoluteUriMinusQueryAndAppPages.Split(SEP.ToCharArray(), StringSplitOptions.RemoveEmptyEntries).ToList();

            var siteRelativeUrl = string.Empty;
            if (urlSegments.Count < 2)
            {
                if (urlSegments.Count == 0)
                {
                    // URL is literally just a root SC URL like https://test.sharepoint.com
                    return null;
                }
            }
            else
            {
                siteRelativeUrl = "/" + string.Join(SEP, urlSegments.Take(2).ToArray());
            }

            var siteUrl = $"{uri.Scheme}://{uri.DnsSafeHost}{siteRelativeUrl}";
            return siteUrl;
        }
        public static bool IsMySiteUrl(string copilotDocContextId)
        {
            return copilotDocContextId.Contains("-my.sharepoint.com");
        }

        /// <summary>
        /// To form path required by https://learn.microsoft.com/en-us/graph/api/site-get
        /// From "https://test.sharepoint.com/sites/test" returns "test.sharepoint.com:/sites/test"
        /// Or, from root site "https://test.sharepoint.com" returns "root"
        /// Or null if none of the above
        /// </summary>
        public static string GetHostAndSiteRelativeUrl(string siteRootUrl)
        {
            const string SEP = "/";
            var urlSegments = siteRootUrl.Split(SEP.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            if (urlSegments.Length < 4)
            {
                if (urlSegments.Length == 2 && urlSegments[1].ToLower().Contains("sharepoint.com"))
                    return "root"; // URL is literally just a root SC URL like https://test.sharepoint.com
                else return null;
            }

            var host = urlSegments[1];
            var siteRelativeUrl = string.Join(SEP, urlSegments.Skip(2).ToArray());

            return $"{host}:/{siteRelativeUrl}";
        }

        /// <summary>
        /// Example: https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2 -> 
        ///          19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2
        /// </summary>
        public static string GetMeetingIdFragmentFromMeetingThreadUrl(string copilotDocContextId)
        {
            const string START = "19:meeting_";
            var start = copilotDocContextId.IndexOf(START);
            if (start == -1)
            {
                return null;
            }
            var meetingId = copilotDocContextId.Substring(start, copilotDocContextId.Length - start);
            return meetingId;
        }

        public static string GetUrlBaseAddressIfValidUrl(string url)
        {
            if (url == null) return null;

            try
            {
                var uri = new Uri(url);
                var siteUrl = uri.GetLeftPart(UriPartial.Path);
                return siteUrl;
            }
            catch (UriFormatException)
            {
                return url;
            }
        }

        /// <summary>
        /// https://contoso.sharepoint.com/sites/site/Shared Documents/
        /// to 
        /// https://contoso.sharepoint.com/sites/site/Shared%20Documents/
        /// Because Uri.IsWellFormedUriString doesn't recognise the 1st example as valid
        /// </summary>
        public static string ConvertSharePointUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            return url.Replace(" ", "%20");
        }

        /// <summary>
        /// Finds 'SPOInsightsDev' in 'Initial Catalog=SPOInsightsDev;' when propName='initial catalog'
        /// </summary>
        /// <param name="fullString"></param>
        /// <param name="propName"></param>
        public static string FindValueForProp(string fullString, string propName)
        {
            int propStart = fullString.IndexOf(propName + "=", StringComparison.CurrentCultureIgnoreCase);
            int propNameLength = propName.Length + "=".Length;
            int propValStart = propStart + propNameLength;
            int propValEnd = fullString.IndexOf(";", propValStart);
            string returnVal = fullString.Substring(propValStart, propValEnd - propValStart);

            return returnVal;
        }

        static char SINGLE_QUOTE = char.Parse("\"");
        public static JObject JsonDecodeFromPropValueString(string input)
        {
            // Try a normal parse
            JObject obj = null;
            try
            {
                obj = JObject.Parse(input);
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                // Ignore
            }
            if (obj != null)
            {
                return obj;
            }

            // Could be because it's quote-enclosed? Remove trailing & leading quotes
            input = input.TrimStart(SINGLE_QUOTE);
            input = input.TrimEnd(SINGLE_QUOTE);

            try
            {
                obj = JObject.Parse(Regex.Unescape(input));
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                // Ignore
            }
            return obj;
        }

        public static string Base64Encode(this string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        public static string TempDirPath
        {
            get
            {

                string tempDir = Path.GetTempPath() + System.Reflection.Assembly.GetEntryAssembly()?.ManifestModule.Name ?? "UnknownProcess";
                return tempDir;
            }
        }

        /// <summary>
        /// Throws FormatException if invalid cipher
        /// </summary>
        public static string Base64Decode(this string base64EncodedData)
        {
            var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }

        static byte[] protectedDataAdditionalEntropy = { 9, 8, 7, 6, 5, 10 };

        /// <summary>
        /// Return encrypted string of another string.
        /// </summary>
        public static byte[] ProtectString(string s)
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(s), protectedDataAdditionalEntropy, DataProtectionScope.CurrentUser);

        }
        /// <summary>
        /// Return string of an encrypted string made from this same machine
        /// </summary>
        public static string UnprotectString(byte[] encryptedPayload)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encryptedPayload, protectedDataAdditionalEntropy, DataProtectionScope.CurrentUser));
        }

        /// <summary>
        /// Find the delta token in a Graph request URL
        /// </summary>
        public static string ExtractCodeFromGraphUrl(string graphUrl)
        {
            // testUrl = https://graph.microsoft.com/v1.0/users/microsoft.graph.delta()?$deltatoken=xxxxxxxxxxxxxxxxxxxxxxxx

            const string TOKEN_START = "$deltatoken=";
            var tokenEqualStart = graphUrl.IndexOf(TOKEN_START);
            var tokenStart = tokenEqualStart + TOKEN_START.Length;
            if (tokenEqualStart > -1)
            {
                var token = graphUrl.Substring(tokenStart, graphUrl.Length - tokenStart);
                return token;
            }

            return null;
        }

        public static string RemoveTrailingSlash(string value)
        {
            if (value == null || value == string.Empty)
            {
                value = string.Empty;
            }

            // Format URL
            if (value.EndsWith("/"))
            {
                value = value.TrimEnd("/".ToCharArray());
            }

            return value;
        }

        public static bool IsJson(this string strInput)
        {
            if (string.IsNullOrWhiteSpace(strInput)) { return false; }
            strInput = strInput.Trim();
            if (strInput.StartsWith("{") && strInput.EndsWith("}") ||  //For object
                strInput.StartsWith("[") && strInput.EndsWith("]"))   //For array
            {
                try
                {
                    JsonDocument.Parse(strInput);
                    return true;
                }
                catch (JsonException)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// To replace "string.TrimStart" as this sometimes doesn't work
        /// </summary>
        public static string TrimStringFromStart(this string strInput, string trim)
        {
            if (string.IsNullOrWhiteSpace(strInput)) throw new ArgumentNullException(nameof(strInput));
            if (string.IsNullOrEmpty(trim))
            {
                throw new ArgumentException($"'{nameof(trim)}' cannot be null or empty.", nameof(trim));
            }

            if (strInput.StartsWith(trim))
            {
                return strInput.Substring(trim.Length, strInput.Length - trim.Length);
            }
            else
                throw new ArgumentException($"'{strInput}' doesn't start with '{trim}'");
        }
        public static bool IsEmail(string input)
        {
            try
            {
                var m = new System.Net.Mail.MailAddress(input);

                return true;
            }
            catch (FormatException)
            {
                return false;
            }

        }


        public static string GenerateNewSalt()
        {
            return BCrypt.Net.BCrypt.GenerateSalt();
        }

        // https://github.com/BcryptNet/bcrypt.net
        public static string GetHashedStringWithSalt(string input)
        {
            // Work factor of "4" is "16" hashing iterations. Hashing done with random salt each time. 
            var hash = BCrypt.Net.BCrypt.HashPassword(input, 4);
            return hash;
        }
        public static string GetHashedStringWithSalt(string input, string salt)
        {
            // Work factor of "4" is "16" hashing iterations. Hashing done with random salt each time. 
            var hash = BCrypt.Net.BCrypt.HashPassword(input, salt, false, BCrypt.Net.HashType.SHA512);
            return hash;
        }


        static byte[] GetHash(string inputString)
        {
            using (var algorithm = SHA256.Create())
                return algorithm.ComputeHash(Encoding.UTF8.GetBytes(inputString));
        }

        public static string GetHashedStringSimple(string inputString)
        {
            var sb = new StringBuilder();
            foreach (byte b in GetHash(inputString))
                sb.Append(b.ToString("X2"));

            return sb.ToString();
        }

        public static bool IsHashedMatch(string input, string hash)
        {
            return BCrypt.Net.BCrypt.Verify(input, hash);
        }

        public static string EnsureMaxLength(string potentiallyLongString, int maxLength)
        {
            if (string.IsNullOrEmpty(potentiallyLongString))
            {
                return string.Empty;
            }
            const string END = "...";

            if (maxLength < END.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }

            if (potentiallyLongString.Length > maxLength)
            {
                return potentiallyLongString.Substring(0, maxLength - END.Length) + END;
            }
            else
            {
                return potentiallyLongString;
            }
        }
    }
}

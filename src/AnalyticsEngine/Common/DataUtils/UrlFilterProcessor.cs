using System;
using System.Collections.Generic;

namespace Common.DataUtils
{
    public class FilterUrlConfig
    {
        public string Url { get; set; } = string.Empty;
        public bool ExactSiteMatch { get; set; }

        internal bool UrlInScope(string urlSiteBaseAddress, string url)
        {
            var ruleUrlLowerCase = Url.ToLower();
            if (string.IsNullOrEmpty(ruleUrlLowerCase))
                throw new ArgumentNullException(nameof(url));

            if (!ExactSiteMatch || string.IsNullOrEmpty(urlSiteBaseAddress))
            {

                // Look for org URLs that match this hit URL. Find the biggest
                if (url.ToLower().StartsWith(ruleUrlLowerCase))
                {
                    return true;
                }
            }
            else
            {
                // Match exactly org url to site (include optional slash)
                if (ruleUrlLowerCase == urlSiteBaseAddress.ToLower() || ruleUrlLowerCase == urlSiteBaseAddress.ToLower() + "/")
                {
                    if (url.ToLower().StartsWith(ruleUrlLowerCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public override string ToString()
        {
            var exact = ExactSiteMatch ? "exact" : "start-with";
            return $"{Url} ({exact})";
        }
    }

    public static class FilterUrlConfigExtensions
    {
        /// <summary>
        /// Is a url in scope for configured filter rules?
        /// </summary>
        /// <param name="matchRules">List of rules of what sites are valid, and in what matching mode</param>
        /// <param name="urlSiteBaseAddress">Site URL the test URL came for - optional</param>
        /// <param name="url">URL to test for</param>
        public static bool UrlInScope(this List<FilterUrlConfig> matchRules, string urlSiteBaseAddress, string url)
        {
            if (matchRules == null)
                throw new ArgumentNullException(nameof(matchRules));

            if (matchRules.Count == 0) return true;

            foreach (var rule in matchRules)
            {
                if (rule.UrlInScope(urlSiteBaseAddress, url))
                    return true;

            }

            // If we've not hit any URLs, return false
            return false;
        }
    }
}

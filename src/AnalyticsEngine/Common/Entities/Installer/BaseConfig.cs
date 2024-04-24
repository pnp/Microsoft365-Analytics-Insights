using System;
using System.Collections.Generic;

namespace Common.Entities.Installer
{
    public abstract class BaseConfig
    {
        public virtual List<string> ValidatInputAndGetErrors() { return new List<string>(); }


        protected bool IsRegexExComplaint(string inputString, string regEx, bool caseSensitive)
        {
            System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            if (!caseSensitive)
            {
                options = System.Text.RegularExpressions.RegexOptions.None;
            }
            return System.Text.RegularExpressions.Regex.IsMatch(inputString, regEx, options);
        }

        /// <summary>
        /// Checks if it's a URL and no trailing slash
        /// </summary>
        protected bool IsValidSPSiteCollectionURL(string url)
        {
            return Uri.IsWellFormedUriString(url, UriKind.Absolute) && !url.EndsWith("/");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Common.Entities.Config
{
    /// <summary>
    /// Model to represent and match Entra ID group names from a filter string.
    /// </summary>
    public class UserGroupsFilterModel
    {
        public List<string> Patterns { get; }

        public UserGroupsFilterModel() : this(string.Empty) { }
        public UserGroupsFilterModel(string filterString)
        {
            if (string.IsNullOrWhiteSpace(filterString))
            {
                Patterns = new List<string>();
            }
            else
            {
                Patterns = filterString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
            }
        }

        /// <summary>
        /// True when the filter would match every group, i.e. at least one pattern is nothing but
        /// wildcards ("*", "**"). Such a filter is not a narrowing at all.
        /// </summary>
        /// <remarks>
        /// Worth asking separately because "match everything" and "match these groups" have very
        /// different costs for a caller that resolves patterns against a directory. Expanding '*' means
        /// enumerating every group and every group's membership only to conclude "everyone", which a
        /// caller can nearly always answer far more cheaply by not filtering at all (issue #297).
        /// </remarks>
        public bool MatchesEverything => Patterns.Any(p => p.Trim('*').Length == 0);

        /// <summary>
        /// True when the filter actually restricts which users are imported: at least one pattern, and none
        /// of them a match-everything wildcard. Anything else (unset, blank, or <c>*</c>) scopes nothing.
        /// </summary>
        public bool IsNarrowing => Patterns.Count > 0 && !MatchesEverything;

        /// <summary>
        /// Identifies which groups this filter selects, whatever the order, case or spacing of its patterns; empty
        /// when it narrows nothing. The user import stores it beside its <c>/users/delta</c> checkpoint, so it can
        /// tell after a restart that the filter has changed since the checkpoint was taken.
        /// </summary>
        public string Fingerprint
        {
            get
            {
                if (!IsNarrowing)
                {
                    return string.Empty;
                }

                // Patterns match case-insensitively (see Matches), so "Pilot" and "pilot" are the same filter.
                var canonical = string.Join("\n", Patterns
                    .Select(p => p.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal));
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                    return "sha256:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
        }

        /// <summary>
        /// Checks if the given group name matches any filter pattern (supports * wildcard).
        /// </summary>
        public bool Matches(string groupName)
        {
            if (string.IsNullOrEmpty(groupName) || Patterns.Count == 0)
                return false;

            foreach (var pattern in Patterns)
            {
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(groupName, regexPattern, RegexOptions.IgnoreCase))
                    return true;
            }
            return false;
        }
    }
}

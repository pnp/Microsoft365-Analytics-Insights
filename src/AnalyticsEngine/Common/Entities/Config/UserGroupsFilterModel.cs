using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Common.Entities.Config
{
    /// <summary>
    /// Model to represent and match Entra ID group names from a filter string.
    /// </summary>
    public class UserGroupsFilterModel
    {
        public List<string> Patterns { get; }

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

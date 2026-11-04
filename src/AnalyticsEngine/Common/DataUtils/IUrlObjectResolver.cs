using System;
using System.Collections.Generic;

namespace DataUtils
{
    public static class IUrlObjectResolver
    {
        private static readonly char[] PathDelimiters = { '/', '?', '#' };

        /// <summary>
        /// For a list of URLs, find the closest match for another URL: the longest that is that URL itself, or one of its
        /// parents.
        /// </summary>
        /// <remarks>
        /// A candidate matches on whole path segments only, so <c>https://contoso.sharepoint.com/sites/hr</c> is a parent of
        /// <c>https://contoso.sharepoint.com/sites/hr/Shared Documents/a.docx</c> but not of
        /// <c>https://contoso.sharepoint.com/sites/hrdocs</c>. Case is ignored, as the SQL collation of the stored URLs ignores
        /// it, and so are trailing slashes.
        /// </remarks>
        public static T GetClosest<T>(this IEnumerable<T> options, string url) where T : IUrlObject
        {
            return GetClosest(options, url, 0);
        }

        /// <param name="shortestMatch">
        /// The length, without trailing slashes, of the shortest candidate accepted. A caller that knows of a boundary above
        /// <paramref name="url"/> that nothing may cross, such as the SharePoint site collection it is in, passes its length.
        /// </param>
        public static T GetClosest<T>(this IEnumerable<T> options, string url, int shortestMatch) where T : IUrlObject
        {
            T bestFit = default;
            if (string.IsNullOrEmpty(url))
            {
                return bestFit;
            }

            var longestMatch = -1;
            foreach (var option in options)
            {
                var candidate = option?.Url;
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                var candidateLength = LengthWithoutTrailingSlashes(candidate);
                if (candidateLength > longestMatch && candidateLength >= shortestMatch && IsSameOrParent(candidate, candidateLength, url))
                {
                    bestFit = option;
                    longestMatch = candidateLength;
                }
            }
            return bestFit;
        }

        /// <summary>
        /// Where the path segment that starts at <paramref name="start"/> ends: the next <c>/</c>, <c>?</c> or <c>#</c>, or the
        /// end of <paramref name="url"/>.
        /// </summary>
        public static int EndOfPathSegment(string url, int start)
        {
            var end = start < url.Length ? url.IndexOfAny(PathDelimiters, start) : -1;
            return end < 0 ? url.Length : end;
        }

        private static bool IsSameOrParent(string parent, int parentLength, string url)
        {
            if (parentLength == 0 || url.Length < parentLength
                || string.Compare(url, 0, parent, 0, parentLength, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            return url.Length == parentLength || Array.IndexOf(PathDelimiters, url[parentLength]) >= 0;
        }

        private static int LengthWithoutTrailingSlashes(string url)
        {
            var length = url.Length;
            while (length > 0 && url[length - 1] == '/')
            {
                length--;
            }
            return length;
        }
    }


    public interface IUrlObject
    {
        string Url { get; }
    }
}

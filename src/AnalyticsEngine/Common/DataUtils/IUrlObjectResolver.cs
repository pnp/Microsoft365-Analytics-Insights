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

        /// <summary>
        /// Where the next shorter candidate parent of <paramref name="url"/> ends, below <paramref name="length"/>: the
        /// position of the last <c>/</c>, <c>?</c> or <c>#</c> before it, or 0 when there is none. Walking down from
        /// <c>url.Length</c> visits every length <see cref="GetClosest{T}(IEnumerable{T}, string, int)"/> could match,
        /// longest first.
        /// </summary>
        internal static int PreviousParentLength(string url, int length)
        {
            return length <= 1 ? 0 : Math.Max(0, url.LastIndexOfAny(PathDelimiters, length - 1));
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

        internal static int LengthWithoutTrailingSlashes(string url)
        {
            var length = url.Length;
            while (length > 0 && url[length - 1] == '/')
            {
                length--;
            }
            return length;
        }
    }

    /// <summary>
    /// URLs, each with a value, that answers <see cref="IUrlObjectResolver.GetClosest{T}(IEnumerable{T}, string, int)"/>'s
    /// question - which is the longest that is a URL itself or one of its parents - without reading every URL it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The URLs are keyed as <c>GetClosest</c> compares them: without case, and without trailing slashes. A lookup then
    /// tries the URL itself and each parent it has on a whole path segment (cut at a <c>/</c>, <c>?</c> or <c>#</c>),
    /// longest first, so it costs one dictionary probe per path segment rather than one comparison per URL held. The
    /// answer is the same: the parents tried are exactly the lengths <c>GetClosest</c> can match.
    /// </para>
    /// <para>
    /// Not thread-safe; callers that share one lock around it.
    /// </para>
    /// </remarks>
    public sealed class UrlPrefixIndex<T>
    {
        private readonly Dictionary<string, T> _byKey = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        public int Count => _byKey.Count;

        public IEnumerable<T> Values => _byKey.Values;

        /// <summary>The key a URL is held under: the URL without its trailing slashes. Null for a null URL.</summary>
        public static string KeyFor(string url)
        {
            if (url == null)
            {
                return null;
            }
            var length = IUrlObjectResolver.LengthWithoutTrailingSlashes(url);
            return length == url.Length ? url : url.Substring(0, length);
        }

        /// <summary>Holds <paramref name="value"/> for <paramref name="url"/>, replacing whatever was held for the same key.</summary>
        public void Set(string url, T value)
        {
            var key = KeyFor(url);
            if (key != null)
            {
                _byKey[key] = value;
            }
        }

        /// <summary>Holds <paramref name="value"/> for <paramref name="url"/> unless something is already held for the same key.</summary>
        public bool TryAdd(string url, T value)
        {
            var key = KeyFor(url);
            if (key == null || _byKey.ContainsKey(key))
            {
                return false;
            }
            _byKey.Add(key, value);
            return true;
        }

        /// <summary>The value held for exactly this URL, whatever its case or trailing slashes.</summary>
        public bool TryGetExact(string url, out T value)
        {
            var key = KeyFor(url);
            if (key == null)
            {
                value = default;
                return false;
            }
            return _byKey.TryGetValue(key, out value);
        }

        /// <summary>
        /// The value held for the longest URL that is <paramref name="url"/> itself or one of its parents, and at least
        /// <paramref name="shortestMatch"/> characters long without trailing slashes: what <c>GetClosest</c> would return
        /// over the same URLs.
        /// </summary>
        public bool TryGetClosest(string url, int shortestMatch, out T value)
        {
            value = default;
            if (string.IsNullOrEmpty(url) || _byKey.Count == 0)
            {
                return false;
            }

            for (var length = url.Length; length > 0 && length >= shortestMatch; length = IUrlObjectResolver.PreviousParentLength(url, length))
            {
                // No key ends in a slash, so a prefix that does cannot be one.
                if (url[length - 1] != '/' && _byKey.TryGetValue(length == url.Length ? url : url.Substring(0, length), out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Clear() => _byKey.Clear();
    }


    public interface IUrlObject
    {
        string Url { get; }
    }
}

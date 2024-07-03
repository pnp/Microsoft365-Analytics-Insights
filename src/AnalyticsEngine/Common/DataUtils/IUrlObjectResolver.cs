using System.Collections.Generic;

namespace DataUtils
{
    public static class IUrlObjectResolver
    {
        /// <summary>
        /// For a list of URLs, find the cloest matching one for another URL. 
        /// </summary>
        public static T GetClosest<T>(this IEnumerable<T> options, string url) where T : IUrlObject
        {
            url = url.ToLower();

            T bestFitSite = default;
            var longestSiteUrl = 0;
            foreach (var urlOption in options)
            {
                // Does this site. Well does it?
                if (urlOption != null && url.StartsWith(urlOption.Url.ToLower()) && longestSiteUrl < urlOption.Url.Length)
                {
                    bestFitSite = urlOption;
                    longestSiteUrl = urlOption.Url.Length;
                }
            }
            return bestFitSite;
        }
    }


    public interface IUrlObject
    {
        string Url { get; }
    }
}

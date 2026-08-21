using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public static class PageableGraphLoaderExtensions
    {
        public static async Task<List<T>> LoadAllPagesWithThrottleRetries<T>(this ManualGraphCallClient client, string url, ILogger logger, bool throwOnNotFound = false, bool throwOnHttpError = false)
        {
            var results = await LoadPageableGraphResponseAllWithOptionalDelta<T>(client, url, logger, null, throwOnNotFound, throwOnHttpError);

            return results;
        }

        public static async Task<List<T>> LoadAllPagesPlusDeltaWithThrottleRetries<T>(this ManualGraphCallClient client, string url, ILogger logger, Func<string, Task> deltaTokenFunc, bool throwOnNotFound = false, bool throwOnHttpError = false)
        {
            var results = await LoadPageableGraphResponseAllWithOptionalDelta<T>(client, url, logger, deltaTokenFunc, throwOnNotFound, throwOnHttpError);

            return results;
        }

        /// <param name="throwOnNotFound">
        /// When true, a 404 is rethrown as <see cref="GraphResourceNotFoundException"/> so the caller can
        /// tell "this resource does not exist" apart from "this resource exists but is empty". Defaults to
        /// false, which preserves the long-standing behaviour of returning the pages loaded so far - most
        /// callers (usage reports, user loaders) genuinely want the partial result.
        /// </param>
        /// <param name="throwOnHttpError">
        /// Strict paging. When true, any non-transient HTTP failure (403, 5xx, an exhausted retry budget)
        /// is rethrown instead of being treated as the end of the result set.
        ///
        /// The default of false truncates: it logs a warning and returns the rows gathered so far, which is
        /// safe only for a caller that treats "fewer rows" as "less data", not as a business outcome. It is
        /// actively dangerous for anything that interprets an empty result - a 403 from a missing
        /// <c>Reports.Read.All</c> grant came back as zero rows, which the Copilot usage-report loaders
        /// reported to the admin as "this tenant has no Copilot licences" while recording a clean import.
        /// Any caller that reads meaning into emptiness, or that writes an import log, must pass true.
        /// </param>
        static async Task<List<T>> LoadPageableGraphResponseAllWithOptionalDelta<T>(ManualGraphCallClient client, string url, ILogger logger, Func<string, Task> deltaTokenFunc, bool throwOnNotFound = false, bool throwOnHttpError = false)
        {
            var allResults = new List<T>();

            int pageCount = 1;

            // Loop until no pages left
            var nextUrl = url;
            while (!string.IsNullOrEmpty(nextUrl))
            {
                var pageSuccess = false;
                PageableGraphResponseWithDelta<T> queryResult = null;
                try
                {
                    queryResult = await client.GetAsyncWithThrottleRetries<PageableGraphResponseWithDelta<T>>(nextUrl);
                    pageSuccess = true;
                }
                catch (GraphResourceNotFoundException ex)
                {
                    // 404 - the resource doesn't exist. Always terminal, never worth a retry.
                    if (throwOnNotFound)
                        throw;

                    pageSuccess = false;
                    logger.LogDebug($"Got 404 loading {typeof(T).Name} page {pageCount} ({ex.GraphErrorCode ?? "unknown"}). " +
                        "Returning results up to current page.");
                    nextUrl = null;
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    pageSuccess = false;

                    // Transient error?
                    if (ex.Message != null && ex.Message.ToLower().Contains("gateway timeout"))
                    {
                        logger.LogInformation($"Got gateway timeout on page {pageCount}. Will retry page.");
                        await Task.Delay(1000);
                    }
                    else
                    {
                        // Strict callers must not mistake a failed download for an empty result set. Rethrow
                        // with the page number so the import log says which page died, not just that one did.
                        if (throwOnHttpError)
                        {
                            logger.LogWarning($"Unexpected HTTP error on page {pageCount} of {typeof(T).Name}: {ex.Message}. " +
                                "Failing the load rather than returning a partial result.");
                            throw;
                        }

                        // ManualGraphCallClient has already logged this at error level, with the URL and the
                        // response body - which is strictly more useful than anything we can add here. Log
                        // only the paging consequence, at warning, so a single failed call produces a single
                        // Application Insights exception record instead of three.
                        logger.LogWarning($"Unexpected HTTP error on page {pageCount}: {ex.Message}. " +
                            "Will not retry page & returning results upto current page.");
                        nextUrl = null;
                    }
                }

                if (pageSuccess)
                {
                    // Another page?
                    nextUrl = queryResult.OdataNextLink;
                    if (nextUrl != null)
                    {
                        pageCount++;
                        // Per-page logging floods the log on large pulls (thousands of pages/cycle), so log
                        // each page at Debug and only a coarse progress line at Information every 50 pages.
                        if (pageCount % 50 == 0)
                        {
                            logger.LogInformation($"Loading {typeof(T).Name} results page #{pageCount}...");
                        }
                        else
                        {
                            logger.LogDebug($"Loading {typeof(T).Name} results page #{pageCount}...");
                        }
                    }
                    else
                    {
                        // Last page of results. Do we have a delta link?
                        if (!string.IsNullOrEmpty(queryResult.DeltaLink) && deltaTokenFunc != null)
                        {
                            await deltaTokenFunc(queryResult.DeltaLink);
                        }
                    }
                    allResults.AddRange(queryResult.PageResults);
                }

            }

            return allResults;
        }
    }

}

using Azure;
using Azure.AI.TextAnalytics;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.PageUpdates;
using WebJob.AppInsightsImporter.Engine.Sql;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Handles page metadata updates: page properties, taxonomy, comments, likes
    /// </summary>
    public class PageUpdateManager
    {
        private readonly ILogger _debugTracer;
        private readonly int _chunkSize;
        private readonly TextAnalyticsClient _textAnalyticsClient = null;
        private readonly AppConfig _config;

        public PageUpdateManager(ILogger debugTracer, AppConfig config) : this(debugTracer, 1000, config)
        {
        }
        public PageUpdateManager(ILogger debugTracer, int chunkSize, AppConfig config)
        {
            _debugTracer = debugTracer ?? throw new ArgumentNullException(nameof(debugTracer));
            _chunkSize = chunkSize;
            _config = config;
            if (chunkSize < 0)
            {
                throw new ArgumentOutOfRangeException("Chunk size must be > 0", nameof(chunkSize));
            }

            // Do we have cognitive services configured?
            var cognitiveConfig = new AppConfig();
            if (!string.IsNullOrEmpty(cognitiveConfig.CognitiveEndpoint) && !string.IsNullOrEmpty(cognitiveConfig.CognitiveKey))
            {
                var credentials = new AzureKeyCredential(cognitiveConfig.CognitiveKey);
                _textAnalyticsClient = new TextAnalyticsClient(new Uri(cognitiveConfig.CognitiveEndpoint), credentials);
            }
        }

        public async Task<List<string>> SaveAll(IEnumerable<PageUpdateEventAppInsightsQueryResult> pageUpdateEvents)
        {
            var updatedUrls = new List<string>();

            // Process all page update events in chunks of 1000 at a time, all in parallel
            var listProc = new ListBatchProcessor<PageUpdateEventAppInsightsQueryResult>(_chunkSize,
                async chunk => await SaveChunk(chunk, updatedUrls));

            listProc.AddRange(pageUpdateEvents);
            listProc.Flush();

#if DEBUG
            _debugTracer.LogInformation($"DEBUG: Waiting for {listProc.BufferSize} URLs to be updated");
#endif
            // Wait for threads to finish
            while (listProc.BufferSize > 0)
            {
                await Task.Delay(1000);
            }

            // Update any URLs that have been updated
            var uniqueUpdatedUrls = updatedUrls.Distinct().ToList();
            using (var db = new AnalyticsEntitiesContext())
            {
                foreach (var u in uniqueUpdatedUrls)
                {
                    var url = await db.urls.Where(e => e.FullUrl == u).SingleOrDefaultAsync();
                    if (url != null)
                    {
                        url.MetadataLastRefreshed = DateTime.UtcNow;
                    }
                }
                await db.SaveChangesAsync();
            }

            _debugTracer.LogInformation($"Updated {uniqueUpdatedUrls.Count} URLs from {pageUpdateEvents.Count()} page-update events");

            return uniqueUpdatedUrls;
        }

        /// <summary>
        /// Saves URLs unless they have been updated in the last 24 hours
        /// </summary>
        async Task SaveChunk(List<PageUpdateEventAppInsightsQueryResult> chunk, List<string> updatedUrls)
        {
#if DEBUG
            _debugTracer.LogInformation($"DEBUG: Updating {chunk.Count} URLs on new thread");
#endif
            using (var context = new AnalyticsEntitiesContext())
            {
                var urlMetadataFieldNameCache = new UrlMetadataFieldNameCache(context);

                var userCache = new UserCache(context);
                var langCache = new LanguageCache(context);
                var urlsForPageUpdateChunk = chunk.Select(e => StringUtils.GetUrlBaseAddressIfValidUrl(e.CustomProperties?.Url)).Distinct().ToList();

                // Get all URLs that have not been updated recently
                var minusMetadataRefreshMinutes = _config.MetadataRefreshMinutes * -1;
                var matchingUrlsNotUpdatedRecently = await context.urls
                    .Where(u => urlsForPageUpdateChunk.Contains(u.FullUrl) && (u.MetadataLastRefreshed == null || u.MetadataLastRefreshed < DbFunctions.AddMinutes(DateTime.Now, minusMetadataRefreshMinutes)))
                    .ToListAsync();

                foreach (var urlToUpdate in matchingUrlsNotUpdatedRecently)
                {
                    var correspondingPageUpdates = chunk.Where(p => StringUtils.GetUrlBaseAddressIfValidUrl(p.CustomProperties?.Url) == urlToUpdate.FullUrl).ToList();

                    // There can be multiple updates across several events. Compile all into one new update
                    var compiledUpdate = new PageUpdateEventAppInsightsQueryResult(correspondingPageUpdates);

                    var urlMetadataUpdated = await UpdateUrlMetadataWith(urlToUpdate, compiledUpdate, context, urlMetadataFieldNameCache);

                    var commentsOrLikesUpdated = await UpdateCommentsOrLikes(urlToUpdate, compiledUpdate, context, userCache, langCache);

                    lock (updatedUrls)
                        if (urlMetadataUpdated || commentsOrLikesUpdated)
                            updatedUrls.Add(urlToUpdate.FullUrl);
                }

                try
                {
                    await context.SaveChangesAsync();
                }
                catch (SqlException ex)
                {
                    _debugTracer.LogError(ex, $"SQL exception '{ex.Message}' when saving page updates for this chunk.");
                }
                catch (DbUpdateException ex)
                {
                    _debugTracer.LogError(ex, $"DbUpdate exception '{CommonExceptionHandler.GetErrorText(ex)}' when saving page updates for this chunk.");
                }
            }

#if DEBUG
            _debugTracer.LogDebug($"DEBUG: Updated {chunk.Count} URLs on new thread");
#endif
        }

        async Task<bool> UpdateCommentsOrLikes(Url url, PageUpdateEventAppInsightsQueryResult correspondingPageUpdate, AnalyticsEntitiesContext db, UserCache userCache, LanguageCache languageCache)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (!url.IsSavedToDB) throw new ArgumentOutOfRangeException(nameof(url), "Cannot save metadata for unsaved URLs");
            if (correspondingPageUpdate is null) throw new ArgumentNullException(nameof(correspondingPageUpdate));

            var urlExistingComments = await db.UrlComments.Where(u => u.UrlID == url.ID).ToListAsync();
            var urlExistingLikes = await db.UrlLikes.Where(u => u.UrlID == url.ID).ToListAsync();

            var newComments = new Dictionary<PageCommentEvent, PageCommentTemp>();

            var commentUpdatesMade = await ProcessCustomAppInsightsEvents(correspondingPageUpdate.CustomProperties.PageComments, urlExistingComments,
                async (PageCommentEvent commentEvent, string email) =>
            {
                // Create temp record for staging table
                var user = await userCache.GetOrCreateNewResource(email, new User { UserPrincipalName = email });

                // Make sure users exist in the DB  
                if (!user.IsSavedToDB)
                {
                    db.users.Add(user);
                    await db.SaveChangesAsync();
                }
                newComments.Add(commentEvent, new PageCommentTemp
                    (commentEvent.Comment, commentEvent.Created ?? DateTime.UtcNow, user.ID, commentEvent.SharePointId.Value, url.ID, commentEvent.ParentSharePointId));
            });

            // Get sentiment scores for new comments, if we have cognitive services configured
            if (_textAnalyticsClient != null && newComments.Count > 0)
            {
                var cognitiveResults = await newComments.Keys
                    .ToTextAnalysisSampleList()
                    .GetCognitiveDataStats(_textAnalyticsClient, _debugTracer);

                if (cognitiveResults != null)
                {
                    foreach (var item in cognitiveResults)
                    {
                        var dbTempOjb = newComments[item.Parent];
                        var lang = await languageCache.GetOrCreateNewResource(item.CognitiveStat.LanguageName, new Language { Name = item.CognitiveStat.LanguageName });

                        // Make sure language exists in the DB
                        if (!lang.IsSavedToDB)
                        {
                            db.Languages.Add(lang);
                            await db.SaveChangesAsync();
                        }
                        dbTempOjb.LanguageID = lang.ID;
                        dbTempOjb.SentimentScore = item.CognitiveStat.SentimentScore;
                    }
                }
            }

            // Save new comments via staging table
            await newComments.Values.ToList().Save(db, _debugTracer);

            // Page likes
            var pageLikeUpdatesMade = await ProcessCustomAppInsightsEvents(correspondingPageUpdate.CustomProperties.Likes, urlExistingLikes, async (UserBasedCustomAIEvent like, string email) =>
            {
                // New like that didn't exist before
                var newLike = new PageLike()
                {
                    Url = url,
                    User = await userCache.GetOrCreateNewResource(email, new User { UserPrincipalName = email }),
                    Created = like.Created ?? DateTime.UtcNow,
                    SpID = like.SharePointId.Value
                };

                db.UrlLikes.Add(newLike);
            });


            return commentUpdatesMade || pageLikeUpdatesMade;
        }

        async Task<bool> ProcessCustomAppInsightsEvents<EVENTTYPE, DBTYPE>(List<EVENTTYPE> eventValues, List<DBTYPE> dbValues, Func<EVENTTYPE, string, Task> callBackOnNew)
            where EVENTTYPE : UserBasedCustomAIEvent where DBTYPE : SPUrlUserRecord
        {
            var updatesMade = false;

            // Insert new not seen before
            foreach (var eventVal in eventValues)
            {
                if (!string.IsNullOrEmpty(eventVal.Email) && eventVal.SharePointId.HasValue)
                {
                    var email = eventVal.Email.ToLower();

                    // Hack: should be an index here preventing multiple records with the same SPID for URL, but apparently it's possible to have mulitple likes/comments from the same user on the same URL
                    var existingDbRecord = dbValues.Where(c => c.SpID == eventVal.SharePointId).FirstOrDefault();
                    if (existingDbRecord == null)
                    {
                        updatesMade = true;
                        await callBackOnNew.Invoke(eventVal, email);
                    }
                }
                else
                {
                    _debugTracer.LogError($"WARNING: Invalid comment/like metadata in event: {eventVal}");
                }
            }

            return updatesMade;
        }

        async Task<bool> UpdateUrlMetadataWith(Url url, PageUpdateEventAppInsightsQueryResult correspondingPageUpdate, AnalyticsEntitiesContext db, UrlMetadataFieldNameCache urlMetadataFieldNameCache)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (!url.IsSavedToDB) throw new ArgumentOutOfRangeException(nameof(url), "Cannot save metadata for unsaved URLs");
            if (correspondingPageUpdate is null) throw new ArgumentNullException(nameof(correspondingPageUpdate));
            var updatesMade = false;

            // Process MM props first as standard props also contain MM props.
            // We'll save MM props 1st so we get the right tag guids saved.
            // Standard prop save ignores any saved value that has a tag guid.
            foreach (var taxonomyProp in correspondingPageUpdate.CustomProperties.TaxonomyProps)
            {
                if (taxonomyProp.IsValid)
                {
                    // Get/create field def
                    var urlPropNameDef = await urlMetadataFieldNameCache.GetResource(taxonomyProp.PropName, () =>
                    {
                        var n = new FileMetadataFieldName() { Name = taxonomyProp.PropName };
                        return Task.FromResult(n);
                    });

                    // Create or update field value?
                    var urlPropVal = await db.FileMetadataPropertyValues
                        .Include(p => p.Field)
                        .Where(u => u.UrlId == url.ID && u.Field.ID == urlPropNameDef.ID).SingleOrDefaultAsync();

                    if (urlPropVal == null)
                    {
                        urlPropVal = new FileMetadataPropertyValue()
                        {
                            Url = url,
                            Field = urlPropNameDef,
                            TagGuid = taxonomyProp.Id
                        };
                        db.FileMetadataPropertyValues.Add(urlPropVal);
                    }
                    urlPropVal.FieldValue = taxonomyProp.Label;
                    urlPropVal.Updated = DateTime.UtcNow;
                    updatesMade = true;
                }
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _debugTracer.LogError(ex, $"DbUpdate exception '{CommonExceptionHandler.GetErrorText(ex)}' when saving page updates for this chunk.");
                return false;
            }

            // Standard props
            foreach (var prop in correspondingPageUpdate.CustomProperties.SimplePropsDic)
            {
                if (prop.Key.Length < 100 && !prop.Key.StartsWith("vti_x005f"))     // Ignore system & over-sized fields (usually the same)
                {
                    // Get/create field def
                    var urlPropNameDef = await urlMetadataFieldNameCache.GetResource(prop.Key, () =>
                    {
                        var n = new FileMetadataFieldName() { Name = prop.Key };
                        return Task.FromResult(n);
                    });

                    // Save field defs if new so the value lookup will succeed
                    if (!urlPropNameDef.IsSavedToDB)
                    {
                        db.FileMetadataFields.Add(urlPropNameDef);

                        try
                        {
                            await db.SaveChangesAsync();
                        }
                        catch (DbUpdateException ex)
                        {
                            _debugTracer.LogError(ex, $"ERROR: DbUpdate exception '{CommonExceptionHandler.GetErrorText(ex)}' when saving page updates for this chunk.");
                            return false;
                        }
                    }

                    // Create or update value?
                    var urlPropVal = await db.FileMetadataPropertyValues
                        .Include(p => p.Field)
                        .Where(u => u.UrlId == url.ID && u.Field.ID == urlPropNameDef.ID).SingleOrDefaultAsync();
                    if (urlPropVal == null)
                    {
                        urlPropVal = new FileMetadataPropertyValue()
                        {
                            Url = url,
                            Field = urlPropNameDef,
                            TagGuid = null
                        };
                        db.FileMetadataPropertyValues.Add(urlPropVal);
                    }

                    // Don't overwrite MM props processed above ^^ 
                    if (urlPropVal.TagGuid == null)
                    {
                        urlPropVal.FieldValue = prop.Value;
                        urlPropVal.Updated = DateTime.UtcNow;
                        updatesMade = true;
                    }
                }
            }

            return updatesMade;
        }
    }
}

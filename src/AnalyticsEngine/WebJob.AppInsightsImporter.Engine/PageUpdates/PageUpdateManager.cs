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
using WebJob.AppInsightsImporter.Engine.PageUpdates.Rules;
using WebJob.AppInsightsImporter.Engine.Sql;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Handles page metadata updates: page properties, taxonomy, comments, likes
    /// </summary>
    public class PageUpdateManager
    {
        private readonly ILogger _logger;
        private readonly int _chunkSize;
        private readonly CognitiveServicesClient _cognitiveClient = null;
        private readonly AppConfig _config;
        private readonly IClock _clock;
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public PageUpdateManager(ILogger logger, AppConfig config, IClock clock = null) : this(logger, 1000, config, clock)
        {
        }
        public PageUpdateManager(ILogger logger, int chunkSize, AppConfig config, IClock clock = null)
            : this(logger, chunkSize, config, clock, null)
        {
        }

        /// <summary>
        /// The full constructor. <paramref name="contextFactory"/> is required here rather than a trailing
        /// optional parameter on the overloads above, because an optional argument is baked in by the
        /// calling compiler and adding one would break already-compiled callers (#381's convention).
        /// </summary>
        public PageUpdateManager(ILogger logger, int chunkSize, AppConfig config, IClock clock, IAnalyticsDbContextFactory contextFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _chunkSize = chunkSize;
            _config = config;
            _clock = clock ?? SystemClock.Instance;
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            if (chunkSize < 0)
            {
                throw new ArgumentOutOfRangeException("Chunk size must be > 0", nameof(chunkSize));
            }

            // Do we have cognitive services configured?
            // Single wrapper for the lifetime of this manager: uses key auth when CognitiveKey
            // is set and auto-falls back to RBAC (ClientSecretCredential) on 403
            // AuthenticationTypeDisabled so we still work when key auth is disabled on the resource.
            var cognitiveConfig = _config ?? new AppConfig();
            _cognitiveClient = cognitiveConfig.CreateCognitiveServicesClient(logger);
        }

        public async Task<List<string>> SaveAll(IEnumerable<PageUpdateEventAppInsightsQueryResult> pageUpdateEvents)
        {
            var updatedUrls = new List<string>();

            // Process page-update events in _chunkSize-sized chunks. ListBatchProcessor's two-argument
            // constructor defaults maxConcurrentBatches to 1, so the chunks run strictly serially - the
            // comment here used to say "chunks of 1000 at a time, all in parallel", and neither half was
            // true. Corrected while adding the context factory below, because the two claims contradicted
            // each other in the same class.
            var listProc = new ListBatchProcessor<PageUpdateEventAppInsightsQueryResult>(_chunkSize,
                async chunk => await SaveChunk(chunk, updatedUrls));

            await listProc.AddRange(pageUpdateEvents);
            await listProc.Flush();

            // Update any URLs that have been updated.
            // Batch the URL lookup in IN-clause-friendly chunks instead of issuing one
            // SingleOrDefaultAsync per URL - at 200k users a busy tenant easily produces
            // thousands of updated URLs per cycle, and per-URL round-trips dominate runtime.
            var uniqueUpdatedUrls = updatedUrls.Distinct().ToList();
            if (uniqueUpdatedUrls.Count > 0)
            {
                const int URL_LOOKUP_BATCH = 1000;
                var now = _clock.UtcNow;
                using (var db = _contextFactory.Create())
                {
                    db.Configuration.AutoDetectChangesEnabled = false;
                    for (int i = 0; i < uniqueUpdatedUrls.Count; i += URL_LOOKUP_BATCH)
                    {
                        var take = Math.Min(URL_LOOKUP_BATCH, uniqueUpdatedUrls.Count - i);
                        var batch = uniqueUpdatedUrls.GetRange(i, take);
                        var urls = await db.urls
                            .Where(u => batch.Contains(u.FullUrl))
                            .ToListAsync();
                        foreach (var u in urls)
                        {
                            u.MetadataLastRefreshed = now;
                        }
                    }
                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                }
            }

            _logger.LogInformation($"Updated {uniqueUpdatedUrls.Count} URLs from {pageUpdateEvents.Count()} page-update events");

            return uniqueUpdatedUrls;
        }

        /// <summary>
        /// Saves URLs unless they have been updated in the last 24 hours
        /// </summary>
        async Task SaveChunk(List<PageUpdateEventAppInsightsQueryResult> chunk, List<string> updatedUrls)
        {
#if DEBUG
            _logger.LogInformation($"DEBUG: Updating {chunk.Count} URLs on new thread");
#endif
            using (var context = _contextFactory.Create())
            {
                var urlMetadataFieldNameCache = new UrlMetadataFieldNameCache(context);

                var userCache = new UserCache(context);
                var langCache = new LanguageCache(context);

                // Pre-bucket the chunk by URL ONCE so the inner update loop is an O(1) dictionary
                // lookup rather than an O(chunk) scan-with-GetUrlBaseAddressIfValidUrl-per-item.
                // Old code: for each matched URL, ran chunk.Where(...).ToList() which re-invokes
                // GetUrlBaseAddressIfValidUrl on every event for every URL -> O(events * urls).
                // At 200k users and large chunk sizes this is the dominant cost.
                var chunkByUrl = PageUpdateGroupingRules.GroupByUrl(chunk);
                var urlsForPageUpdateChunk = chunkByUrl.Keys.ToList();

                // Get all URLs that have not been updated recently
                var staleBeforeUtc = PageUpdateRefreshPolicy.StaleBeforeUtc(_config.MetadataRefreshMinutes, _clock.UtcNow);
                var matchingUrlsNotUpdatedRecently = await context.urls
                    .Where(u => urlsForPageUpdateChunk.Contains(u.FullUrl) && (u.MetadataLastRefreshed == null || u.MetadataLastRefreshed < staleBeforeUtc))
                    .ToListAsync();

                foreach (var urlToUpdate in matchingUrlsNotUpdatedRecently)
                {
                    if (!chunkByUrl.TryGetValue(urlToUpdate.FullUrl, out var correspondingPageUpdates))
                        continue;

                    try
                    {
                        // There can be multiple updates across several events. Compile all into one new update
                        var compiledUpdate = new PageUpdateEventAppInsightsQueryResult(correspondingPageUpdates);

                        var urlMetadataUpdated = await UpdateUrlMetadataWith(urlToUpdate, compiledUpdate, context, urlMetadataFieldNameCache);

                        var commentsOrLikesUpdated = await UpdateCommentsOrLikes(urlToUpdate, compiledUpdate, context, userCache, langCache);

                        lock (updatedUrls)
                            if (urlMetadataUpdated || commentsOrLikesUpdated)
                                updatedUrls.Add(urlToUpdate.FullUrl);
                    }
                    catch (Exception ex)
                    {
                        // Don't let one bad page-update abort the whole chunk (up to 1000 URLs). The most
                        // likely culprit is an uncaught DbUpdateException from a comment/like whose user or
                        // language insert fails - which previously escaped every catch above it and stalled
                        // the importer for months. Log the full error, skip this URL, and keep going.
                        _logger.LogError(ex, $"Failed applying page-update metadata for URL '{urlToUpdate.FullUrl}': {CommonExceptionHandler.GetErrorText(ex)}. Skipping this URL.");
                    }
                }

                try
                {
                    await context.SaveChangesAsync();
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, $"SQL exception '{ex.Message}' when saving page updates for this chunk.");
                }
                catch (DbUpdateException ex)
                {
                    _logger.LogError(ex, $"DbUpdate exception '{CommonExceptionHandler.GetErrorText(ex)}' when saving page updates for this chunk.");
                }
            }

#if DEBUG
            _logger.LogDebug($"DEBUG: Updated {chunk.Count} URLs on new thread");
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
                    (commentEvent.Comment, commentEvent.Created ?? _clock.UtcNow, user.ID, commentEvent.SharePointId.Value, url.ID, commentEvent.ParentSharePointId));
            });

            // Get sentiment scores for new comments, if we have cognitive services configured
            if (PageUserEventRules.ShouldRequestSentiment(_cognitiveClient != null, newComments.Count))
            {
                var cognitiveResults = await newComments.Keys
                    .ToTextAnalysisSampleList()
                    .GetCognitiveDataStats(_cognitiveClient, _logger);

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
            await newComments.Values.ToList().Save(db, _logger);

            // Page likes
            var pageLikeUpdatesMade = await ProcessCustomAppInsightsEvents(correspondingPageUpdate.CustomProperties.Likes, urlExistingLikes, async (UserBasedCustomAIEvent like, string email) =>
            {
                // New like that didn't exist before
                var newLike = new PageLike()
                {
                    Url = url,
                    User = await userCache.GetOrCreateNewResource(email, new User { UserPrincipalName = email }),
                    Created = like.Created ?? _clock.UtcNow,
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

            // Insert new not seen before. The validation + de-duplication rule lives in PageUserEventRules
            // so it can be asserted without a database; the decisions come back in input order, so the log
            // sequence and the "what has already happened when a create throws" behaviour are unchanged.
            foreach (var decision in PageUserEventRules.Classify(eventValues, dbValues))
            {
                if (decision.Outcome == PageUserEventOutcome.Invalid)
                {
                    _logger.LogError($"WARNING: Invalid comment/like metadata in event: {decision.Event}");
                    continue;
                }

                // Hack: should be an index here preventing multiple records with the same SPID for URL, but apparently it's possible to have mulitple likes/comments from the same user on the same URL
                if (decision.Outcome == PageUserEventOutcome.New)
                {
                    updatesMade = true;
                    await callBackOnNew.Invoke(decision.Event, decision.NormalisedEmail);
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
                    urlPropVal.Updated = _clock.UtcNow;
                    updatesMade = true;
                }
            }
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, $"DbUpdate exception '{CommonExceptionHandler.GetErrorText(ex)}' when saving page updates for this chunk.");
                return false;
            }

            // Standard props
            foreach (var prop in correspondingPageUpdate.CustomProperties.SimplePropsDic)
            {
                if (UrlMetadataPropertyRules.IsImportableSimpleProp(prop.Key))
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
                            _logger.LogError(ex, $"ERROR: DbUpdate exception '{CommonExceptionHandler.GetErrorText(ex)}' when saving page updates for this chunk.");
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
                        urlPropVal.Updated = _clock.UtcNow;
                        updatesMade = true;
                    }
                }
            }

            return updatesMade;
        }
    }
}

using Azure;
using Azure.AI.TextAnalytics;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Validation;
using Microsoft.Data.SqlClient;
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

        /// <summary>
        /// Saves the metadata, comments and likes the events carry, and marks each page whose changes were saved as
        /// refreshed. Returns those pages' URLs.
        /// </summary>
        /// <remarks>
        /// A page whose changes cannot be saved is skipped and logged, and is not marked as refreshed, so the
        /// tracker's next send for it is read rather than ignored for <see cref="AppConfig.MetadataRefreshMinutes"/>.
        /// A transient SQL failure (see <see cref="TransientSqlRetry.IsTransient"/>) is not skipped: it is thrown, so
        /// the page-update section fails and the day is retried.
        /// </remarks>
        public async Task<List<string>> SaveAll(IEnumerable<PageUpdateEventAppInsightsQueryResult> pageUpdateEvents)
        {
            var updatedUrls = new List<string>();
            var failedUrlIds = new List<int>();

            // Process page-update events in _chunkSize-sized chunks (1000 in production - see
            // PageUpdatesSaveExtension). ListBatchProcessor's two-argument constructor defaults
            // maxConcurrentBatches to 1, so the chunks run strictly SERIALLY: the comment here used to
            // say "all in parallel", which was never true. Corrected while adding the context factory
            // below, because SqlPageUpdatePersistenceManager's doc had inherited the same wrong claim.
            var listProc = new ListBatchProcessor<PageUpdateEventAppInsightsQueryResult>(_chunkSize,
                async chunk => await SaveChunk(chunk, updatedUrls, failedUrlIds));

            await listProc.AddRange(pageUpdateEvents);
            await listProc.Flush();

            // Mark the pages whose changes were saved as refreshed. SaveChunk lists a page only once its save succeeded.
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
            if (failedUrlIds.Count > 0)
            {
                _logger.LogError($"The metadata of {failedUrlIds.Count:n0} page(s) could not be saved (errors above, by URL id). " +
                    "They are not marked as refreshed, so their metadata is read again the next time the tracker sends it.");
            }

            return uniqueUpdatedUrls;
        }

        /// <summary>
        /// Saves URLs unless they have been updated in the last 24 hours
        /// </summary>
        /// <remarks>
        /// Each page is a unit of work: its changes are saved together before the next page is read, and only then is
        /// it listed in <paramref name="updatedUrls"/>, which <see cref="SaveAll"/> marks as refreshed. A page that fails
        /// loses only its own unsaved changes, which are discarded so the next page's save does not try to write them
        /// again. The whole chunk used to share one final save whose failure was logged and swallowed, after every page
        /// had already been listed as updated - so those pages were marked as refreshed with nothing saved.
        /// </remarks>
        async Task SaveChunk(List<PageUpdateEventAppInsightsQueryResult> chunk, List<string> updatedUrls, List<int> failedUrlIds)
        {
#if DEBUG
            _logger.LogInformation($"DEBUG: Updating {chunk.Count} URLs on new thread");
#endif
            using (var context = _contextFactory.Create())
            {
                // Changes are detected once, just before each save (SaveDetectingChangesAsync), instead of on every Add,
                // which with automatic detection re-scans everything the context tracks.
                context.Configuration.AutoDetectChangesEnabled = false;

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

                // Get all URLs that have not been updated recently. Not tracked: nothing here changes a URL, and a
                // tracked one would collect every value attached for it in Url.UrlMetadataProps.
                var staleBeforeUtc = PageUpdateRefreshPolicy.StaleBeforeUtc(_config.MetadataRefreshMinutes, _clock.UtcNow);
                var matchingUrlsNotUpdatedRecently = await context.urls
                    .AsNoTracking()
                    .Where(u => urlsForPageUpdateChunk.Contains(u.FullUrl) && (u.MetadataLastRefreshed == null || u.MetadataLastRefreshed < staleBeforeUtc))
                    .ToListAsync();

                // What these pages already have stored, read for the whole chunk rather than with one query per
                // property of every page, and two more per page for its comments and likes
                var stored = await StoredPageData.LoadAsync(context, matchingUrlsNotUpdatedRecently.Select(u => u.ID).ToList());

                foreach (var urlToUpdate in matchingUrlsNotUpdatedRecently)
                {
                    if (!chunkByUrl.TryGetValue(urlToUpdate.FullUrl, out var correspondingPageUpdates))
                        continue;

                    // This page's tracked rows, forgotten once it is done so the context only ever tracks one page's
                    // values, and the cached users, languages and field names
                    var pageEntities = new List<object>();
                    try
                    {
                        // There can be multiple updates across several events. Compile all into one new update
                        var compiledUpdate = new PageUpdateEventAppInsightsQueryResult(correspondingPageUpdates);

                        var urlMetadataUpdated = await UpdateUrlMetadataWith(urlToUpdate, compiledUpdate, context, urlMetadataFieldNameCache,
                            stored.ValuesFor(urlToUpdate.ID), pageEntities);

                        var commentsOrLikesUpdated = await UpdateCommentsOrLikes(urlToUpdate, compiledUpdate, context, userCache, langCache,
                            stored.CommentsFor(urlToUpdate.ID), stored.LikesFor(urlToUpdate.ID), pageEntities);

                        await SaveDetectingChangesAsync(context);

                        lock (updatedUrls)
                            if (urlMetadataUpdated || commentsOrLikesUpdated)
                                updatedUrls.Add(urlToUpdate.FullUrl);
                    }
                    catch (Exception ex) when (!TransientSqlRetry.IsTransient(ex))
                    {
                        // Don't let one bad page-update abort the whole chunk (up to 1000 URLs). The most
                        // likely culprit is an uncaught DbUpdateException from a comment/like whose user or
                        // language insert fails - which previously escaped every catch above it and stalled
                        // the importer for months. Skip this URL and keep going; a transient SQL failure is
                        // not caught here, so the section fails and the day is retried.
                        DiscardUnsavedChanges(context);
                        lock (failedUrlIds)
                            failedUrlIds.Add(urlToUpdate.ID);

                        // By id: the URL and anything SQL Server quotes from it are tenant content (#804)
                        _logger.LogError($"Failed saving page-update metadata for URL id {urlToUpdate.ID}: {DescribeFailure(ex)}. " +
                            "Skipping this URL; it is not marked as refreshed.");
                    }
                    finally
                    {
                        Forget(context, pageEntities);
                    }
                }
            }

#if DEBUG
            _logger.LogDebug($"DEBUG: Updated {chunk.Count} URLs on new thread");
#endif
        }

        /// <summary>
        /// Saves with change detection run first. Automatic detection is off on the chunk's context, so without this a
        /// value changed in place would silently not be saved. Every save in this class goes through here, including
        /// the ones that store a new user, language or field name before the page's own save.
        /// </summary>
        static async Task SaveDetectingChangesAsync(DbContext db)
        {
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Forgets the failed page's unsaved changes - anything added, and the values it changed in place - so that the
        /// next page's save does not try to write them again and fail too.
        /// </summary>
        static void DiscardUnsavedChanges(DbContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        static void Forget(DbContext db, IEnumerable<object> entities)
        {
            foreach (var entity in entities)
            {
                var entry = db.Entry(entity);
                if (entry.State != EntityState.Detached)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        /// <summary>
        /// The failure without anything from the page itself: the exception types, and for SQL Server its error number
        /// and message with any value it quoted removed (<see cref="SqlErrorMessages.WithoutValues"/>). Other messages
        /// are left out, because they can quote a value too.
        /// </summary>
        internal static string DescribeFailure(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is SqlException sql)
                {
                    parts.Add($"{nameof(SqlException)} {sql.Number}: {SqlErrorMessages.WithoutValues(sql.Message)}");
                }
                else if (e is DbEntityValidationException validation)
                {
                    var properties = validation.EntityValidationErrors
                        .SelectMany(r => r.ValidationErrors.Select(v => v.PropertyName))
                        .Distinct();
                    parts.Add($"{nameof(DbEntityValidationException)} ({string.Join(", ", properties)})");
                }
                else
                {
                    parts.Add(e.GetType().Name);
                }
            }
            return string.Join(" > ", parts);
        }

        async Task<bool> UpdateCommentsOrLikes(Url url, PageUpdateEventAppInsightsQueryResult correspondingPageUpdate, AnalyticsEntitiesContext db, UserCache userCache, LanguageCache languageCache,
            List<PageComment> urlExistingComments, List<PageLike> urlExistingLikes, List<object> pageEntities)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (!url.IsSavedToDB) throw new ArgumentOutOfRangeException(nameof(url), "Cannot save metadata for unsaved URLs");
            if (correspondingPageUpdate is null) throw new ArgumentNullException(nameof(correspondingPageUpdate));

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
                    await SaveDetectingChangesAsync(db);
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
                            await SaveDetectingChangesAsync(db);
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
                // New like that didn't exist before. By the URL's id: the URL is not tracked here, so linking the
                // entity would insert it again
                var newLike = new PageLike()
                {
                    UrlID = url.ID,
                    User = await userCache.GetOrCreateNewResource(email, new User { UserPrincipalName = email }),
                    Created = like.Created ?? _clock.UtcNow,
                    SpID = like.SharePointId.Value
                };

                db.UrlLikes.Add(newLike);
                pageEntities.Add(newLike);
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
                    // Not the event itself: its text is the author's e-mail address (#804)
                    var missing = string.IsNullOrEmpty(decision.Event.Email) ? "author e-mail" : "SharePoint id";
                    _logger.LogError($"WARNING: Invalid comment/like metadata in event: {decision.Event.GetType().Name} with no {missing}");
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

        async Task<bool> UpdateUrlMetadataWith(Url url, PageUpdateEventAppInsightsQueryResult correspondingPageUpdate, AnalyticsEntitiesContext db, UrlMetadataFieldNameCache urlMetadataFieldNameCache,
            Dictionary<int, FileMetadataPropertyValue> storedValuesByFieldId, List<object> pageEntities)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (!url.IsSavedToDB) throw new ArgumentOutOfRangeException(nameof(url), "Cannot save metadata for unsaved URLs");
            if (correspondingPageUpdate is null) throw new ArgumentNullException(nameof(correspondingPageUpdate));
            var updatesMade = false;
            var now = _clock.UtcNow;

            // A taxonomy field's name is stored like any other, so the same rule decides which can be
            var taxonomyProps = correspondingPageUpdate.CustomProperties.TaxonomyProps
                .Where(t => t.IsValid && UrlMetadataPropertyRules.IsImportableSimpleProp(t.PropName))
                .ToList();
            var simpleProps = correspondingPageUpdate.CustomProperties.SimplePropsDic
                .Where(p => UrlMetadataPropertyRules.IsImportableSimpleProp(p.Key))
                .ToList();

            // Every field this page writes, with any definition not seen before saved first, so that the values below
            // change only in memory until the page is saved
            var fields = new Dictionary<string, FileMetadataFieldName>();
            foreach (var name in taxonomyProps.Select(t => t.PropName).Concat(simpleProps.Select(p => p.Key)))
            {
                if (!fields.ContainsKey(name))
                {
                    fields.Add(name, await GetOrCreateFieldAsync(name, db, urlMetadataFieldNameCache));
                }
            }

            // This page's stored value for a field, attached so a change to it is saved, or a new one
            FileMetadataPropertyValue ValueToWrite(FileMetadataFieldName field, FileMetadataPropertyValue stored)
            {
                if (stored != null)
                {
                    if (db.Entry(stored).State == EntityState.Detached)
                    {
                        db.FileMetadataPropertyValues.Attach(stored);
                        pageEntities.Add(stored);
                    }
                    return stored;
                }

                var value = new FileMetadataPropertyValue { UrlId = url.ID, FieldId = field.ID };
                db.FileMetadataPropertyValues.Add(value);
                pageEntities.Add(value);
                storedValuesByFieldId[field.ID] = value;
                return value;
            }

            // Process MM props first as standard props also contain MM props.
            // Standard prop save ignores any saved value that has a tag guid.
            foreach (var taxonomyProp in taxonomyProps)
            {
                var field = fields[taxonomyProp.PropName];
                storedValuesByFieldId.TryGetValue(field.ID, out var stored);
                var urlPropVal = ValueToWrite(field, stored);

                // The term's id is set on a stored value too. One a tracker that sent no taxonomy fields left holds the
                // raw "id;#Label|termGuid" text with no id, which the standard pass below would otherwise write back
                // over the label; and a page tagged with another term since has the old term's id.
                urlPropVal.TagGuid = taxonomyProp.Id;
                urlPropVal.FieldValue = taxonomyProp.Label;
                urlPropVal.Updated = now;
                updatesMade = true;
            }

            // Standard props
            foreach (var prop in simpleProps)
            {
                var field = fields[prop.Key];
                storedValuesByFieldId.TryGetValue(field.ID, out var stored);

                // Don't overwrite MM props processed above ^^ 
                if (stored?.TagGuid != null)
                {
                    continue;
                }

                var urlPropVal = ValueToWrite(field, stored);
                urlPropVal.FieldValue = prop.Value;
                urlPropVal.Updated = now;
                updatesMade = true;
            }

            return updatesMade;
        }

        /// <summary>The field's definition, saved first if it is new so values can refer to it by id.</summary>
        static async Task<FileMetadataFieldName> GetOrCreateFieldAsync(string name, AnalyticsEntitiesContext db, UrlMetadataFieldNameCache urlMetadataFieldNameCache)
        {
            var field = await urlMetadataFieldNameCache.GetResource(name, () => Task.FromResult(new FileMetadataFieldName() { Name = name }));
            if (!field.IsSavedToDB)
            {
                db.FileMetadataFields.Add(field);
                await SaveDetectingChangesAsync(db);
            }
            return field;
        }

        /// <summary>
        /// What a chunk's pages already have stored - metadata values, comments and likes - read with one query of each
        /// per <see cref="MaxUrlIdsPerRead"/> pages. None of it is tracked: a value is attached only when a page changes
        /// it, and comments and likes are only compared with.
        /// </summary>
        sealed class StoredPageData
        {
            /// <summary>Each id is a value in an IN list, so this stays well under SQL Server's 2,100-parameter limit.</summary>
            const int MaxUrlIdsPerRead = 1000;

            readonly Dictionary<int, Dictionary<int, FileMetadataPropertyValue>> _valuesByUrlId;
            readonly ILookup<int, PageComment> _commentsByUrlId;
            readonly ILookup<int, PageLike> _likesByUrlId;

            StoredPageData(List<FileMetadataPropertyValue> values, List<PageComment> comments, List<PageLike> likes)
            {
                // One value per URL and field: IX_url_id_field_id is unique
                _valuesByUrlId = values
                    .GroupBy(v => v.UrlId)
                    .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.FieldId));
                _commentsByUrlId = comments.ToLookup(c => c.UrlID);
                _likesByUrlId = likes.ToLookup(l => l.UrlID);
            }

            public static async Task<StoredPageData> LoadAsync(AnalyticsEntitiesContext db, List<int> urlIds)
            {
                var values = new List<FileMetadataPropertyValue>();
                var comments = new List<PageComment>();
                var likes = new List<PageLike>();
                for (var i = 0; i < urlIds.Count; i += MaxUrlIdsPerRead)
                {
                    var batch = urlIds.GetRange(i, Math.Min(MaxUrlIdsPerRead, urlIds.Count - i));
                    values.AddRange(await db.FileMetadataPropertyValues.AsNoTracking().Where(v => batch.Contains(v.UrlId)).ToListAsync());
                    comments.AddRange(await db.UrlComments.AsNoTracking().Where(c => batch.Contains(c.UrlID)).ToListAsync());
                    likes.AddRange(await db.UrlLikes.AsNoTracking().Where(l => batch.Contains(l.UrlID)).ToListAsync());
                }
                return new StoredPageData(values, comments, likes);
            }

            /// <summary>The URL's values by field id. New values are added to it as they are created.</summary>
            public Dictionary<int, FileMetadataPropertyValue> ValuesFor(int urlId)
            {
                if (!_valuesByUrlId.TryGetValue(urlId, out var values))
                {
                    values = new Dictionary<int, FileMetadataPropertyValue>();
                    _valuesByUrlId.Add(urlId, values);
                }
                return values;
            }

            public List<PageComment> CommentsFor(int urlId) => _commentsByUrlId[urlId].ToList();

            public List<PageLike> LikesFor(int urlId) => _likesByUrlId[urlId].ToList();
        }
    }
}

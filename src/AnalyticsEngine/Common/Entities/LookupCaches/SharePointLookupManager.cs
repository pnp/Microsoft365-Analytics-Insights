using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities
{
    /// <summary>
    /// Gets or creates various lookup objects for SPO Insights. Caches responses.
    /// </summary>
    public class SharePointLookupManager
    {
        private SPWebResolver _spWebResolver = null;
        #region Properties

        public Dictionary<string, Url> UrlCache { get; } = null;

        public List<Hit> HitCache { get; } = null;

        #endregion

        #region Constructors & Privates

        private Dictionary<string, EventOperation> _operationsCache = null;
        private Dictionary<string, SPEventFileExtension> _fileExtCache = null;
        private Dictionary<string, SPEventFileName> _fileNameCache = null;

        private O365ClientApplicationCache _o365ClientAppCache = null;

        private readonly Dictionary<string, SPEventType> _eventTypeCache = null;
        private readonly AnalyticsEntitiesContext _db;
        private readonly SharePointWebIndex _webIndex;

        public SharePointLookupManager(AnalyticsEntitiesContext db) : this(db, null)
        {
        }

        /// <param name="webIndex">
        /// The webs and site collections in SQL, shared with the other save batches of the import cycle. Null gives this
        /// manager an index of its own, read in full the first time a web is looked up.
        /// </param>
        public SharePointLookupManager(AnalyticsEntitiesContext db, SharePointWebIndex webIndex)
        {
            this.HitCache = new List<Hit>();
            UrlCache = new Dictionary<string, Url>();

            _operationsCache = new Dictionary<string, EventOperation>();
            _fileExtCache = new Dictionary<string, SPEventFileExtension>();
            _fileNameCache = new Dictionary<string, SPEventFileName>();
            _eventTypeCache = new Dictionary<string, SPEventType>();
            _o365ClientAppCache = new O365ClientApplicationCache(db);
            _db = db;
            _webIndex = webIndex ?? new SharePointWebIndex();
        }

        #endregion

        #region O365 ClientApp

        public async Task<O365ClientApplication> GetClientApp(string clientAppId)
        {
            var lookup = await _o365ClientAppCache.GetResource(clientAppId);
            if (lookup == null)
            {
                lookup = await _o365ClientAppCache.GetResource(O365ClientApplication.UNKNOWN_CLIENT_APP_ID.ToString());
            }
            return lookup;
        }
        public async Task<O365ClientApplication> GetClientApp(Guid clientAppId)
        {
            return await GetClientApp(clientAppId.ToString());
        }

        internal class O365ClientApplicationCache : DBLookupCache<O365ClientApplication>
        {
            public O365ClientApplicationCache(AnalyticsEntitiesContext context) : base(context) { }

            public override DbSet<O365ClientApplication> EntityStore => this.DB.O365ClientApplications;

            public async override Task<O365ClientApplication> Load(string searchKey)
            {
                return await EntityStore.SingleOrDefaultAsync(t => t.ClientApplicationId == new Guid(searchKey));
            }

        }
        #endregion

        /// <summary>
        /// Gets the web for an audit event, creating it and its site collection when <paramref name="createIfNoneFound"/>.
        /// With it, <paramref name="webUrl"/> is the web's own address (an audit record's <c>SiteUrl</c>); without it, an
        /// address inside a web, such as a file's. See <see cref="SPWebResolver.GetWeb"/>.
        /// </summary>
        public async Task<Web> GetWebOrCreateWebPlusSite(string webUrl, bool createIfNoneFound)
        {
            if (_spWebResolver == null)
            {
                // Once per batch, before its first lookup: picks up the sites this batch's staging merge has just inserted
                // and the webs earlier batches saved. After the index's first load this reads only the rows added since.
                await _webIndex.RefreshAsync(_db);
                _spWebResolver = new SPWebResolver(_webIndex, _db);
            }

            return _spWebResolver.GetWeb(webUrl, createIfNoneFound);
        }

        /// <summary>
        /// For a save that failed: it may have collided with a web or site the shared index had not read, so the next batch
        /// reads them all again.
        /// </summary>
        public void ForgetWebIndexAfterFailedSave() => _webIndex.Invalidate();

        #region Hit-Specific Lookups


        /// <summary>
        /// Return a hit by hit time, session, and url
        /// </summary>
        public Hit GetHitByDuplicateCheck(DateTime eventTime, string sessionId, string url)
        {
            // Take into consideration SQL date-time rounding. DT2 fiels support greater accuracy
            DateTime sqlStoredDate = new System.Data.SqlTypes.SqlDateTime(eventTime).Value;

            var cahedHits = HitCache.Where(
                h => h.hit_timestamp.Equals(sqlStoredDate) &&
                h.session.ai_session_id == sessionId &&
                h.url.FullUrl == url
            );

            if (cahedHits != null && cahedHits.Any())
            {
                return cahedHits.First();
            }
            else
            {
                // No need to convert DT value for SQL queriies
                var dbHits = _db.hits.Where(h => h.hit_timestamp.Equals(sqlStoredDate) &&
                                                    h.session.ai_session_id == sessionId &&
                                                    h.url.FullUrl == url);
                if (dbHits.Any())
                {
                    Hit duplicateHit = dbHits.First();
                    HitCache.Add(duplicateHit);
                    return duplicateHit;
                }
                else
                {
                    return null;
                }
            }

        }

        #endregion

        #region Event-Specific Lookups

        public SPEventFileName GetOrCreateEventFilename(string filename)
        {
            // Sanity
            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentNullException("filename");
            }

            string key = filename.ToLower();
            if (!_fileNameCache.ContainsKey(key))
            {
                SPEventFileName file = _db.event_file_names.Where(f => f.Name == filename).FirstOrDefault();

                if (file == null)
                {
                    file = new SPEventFileName();
                    file.Name = filename;

                    // Add lookup to DB, as it's likely being added outside the context of a new event.
                    _db.event_file_names.Add(file);
                }

                _fileNameCache.Add(key, file);
            }

            return _fileNameCache[key];
        }

        public SPEventType GetEventType(string eventType)
        {
            // Sanity. Return no lookup if no data
            if (string.IsNullOrEmpty(eventType))
            {
                throw new ArgumentNullException("eventType");
            }


            string key = eventType.ToLower();

            if (!_eventTypeCache.ContainsKey(key))
            {
                var types = (from allRecs in _db.event_types
                             where allRecs.type_name == eventType
                             select allRecs);

                SPEventType et = null;
                if (types != null && types.Any())
                {
                    et = types.First();
                }
                else
                {
                    et = new SPEventType();
                    et.type_name = eventType;

                    // Add lookup to DB, as it's likely being added outside the context of a new event.
                    _db.event_types.Add(et);
                }

                _eventTypeCache.Add(key, et);
            }

            return _eventTypeCache[key];
        }

        public EventOperation GetOrCreateOperation(string operation)
        {
            // Sanity. Return no lookup if no data
            if (string.IsNullOrEmpty(operation))
            {
                throw new ArgumentNullException("operation");
            }

            string key = operation.ToLower();

            // Seen lower-case key in cache?
            if (!_operationsCache.ContainsKey(key))
            {
                // Seen in DB?
                EventOperation dbOp = _db.event_operations.Where(e => e.Name == operation).SingleOrDefault();
                if (dbOp == null)
                {
                    // Create new & cache
                    dbOp = new EventOperation() { Name = operation };

                    // Add lookup to DB, as it's likely being added outside the context of a new event.
                    _db.event_operations.Add(dbOp);
                }
                _operationsCache.Add(key, dbOp);
            }
            return _operationsCache[key];
        }

        public SPEventFileExtension GetOrCreateFileExtension(string sourceFileExtension)
        {

            // Sanity. Return no lookup if no data
            if (string.IsNullOrEmpty(sourceFileExtension))
            {
                throw new ArgumentNullException("sourceFileExtension");
            }

            string key = sourceFileExtension.ToLower();

            // Seen lower-case key in cache?
            if (!_fileExtCache.ContainsKey(key))
            {
                // Seen in DB?
                SPEventFileExtension l = _db.event_file_ext.Where(e => e.extension_name == sourceFileExtension).SingleOrDefault();
                if (l == null)
                {
                    // Create new & cache
                    l = new SPEventFileExtension() { extension_name = sourceFileExtension };
                    // Add lookup to DB, as it's likely being added outside the context of a new event.
                    _db.event_file_ext.Add(l);
                }
                _fileExtCache.Add(key, l);
            }
            return _fileExtCache[key];
        }

        public Url GetUrl(string v)
        {
            throw new NotImplementedException();
        }


        #endregion
    }
}

using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    public static class PageUpdatesSaveExtension
    {

        public static Task<int> SavePageUpdatesToSQL(this CustomEventsResultCollection eventList, ILogger logger, AppConfig config)
        {
            return SavePageUpdatesToSQL(eventList, logger, config, DefaultAnalyticsDbContextFactory.Instance);
        }

        /// <summary>
        /// As above, with the context factory supplied (#368/#369). A new overload rather than a trailing
        /// optional parameter, which would be binary-breaking for already-compiled callers.
        /// </summary>
        public static async Task<int> SavePageUpdatesToSQL(this CustomEventsResultCollection eventList, ILogger logger, AppConfig config, IAnalyticsDbContextFactory contextFactory)
        {
            if (eventList.Rows.Count == 0) return 0;

            var updateManager = new PageUpdateManager(logger, 1000, config, null, contextFactory);

            // Filter from custom events which are page-updates
            var pageUpdates = eventList.Rows
                .Where(r => r.GetType() == typeof(PageUpdateEventAppInsightsQueryResult))
                .Cast<PageUpdateEventAppInsightsQueryResult>();

            var updatedUrls = await updateManager.SaveAll(pageUpdates);
            return updatedUrls.Count;
        }
    }

    public class UrlMetadataFieldNameCache : ObjectByIdCache<FileMetadataFieldName>
    {
        private readonly AnalyticsEntitiesContext _context;

        public UrlMetadataFieldNameCache(AnalyticsEntitiesContext context)
        {
            _context = context;
        }

        public override async Task<FileMetadataFieldName> Load(string id)
        {
            // Hoist ToLowerInvariant out of the EF query — LINQ-to-Entities can translate
            // String.ToLower() but NOT String.ToLowerInvariant() and throws NotSupportedException
            // at execution time if it appears inside an IQueryable expression tree.
            var lookupName = id?.ToLowerInvariant();
            return await _context.FileMetadataFields.Where(e => e.Name == lookupName).FirstOrDefaultAsync();
        }
    }

}

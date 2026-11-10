using Common.Entities.Config;
using Common.Entities.UserScope;
using Microsoft.Extensions.Logging;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// The real SQL save path for audit events, with a Copilot metadata loader that never calls Microsoft Graph.
    /// </summary>
    /// <remarks>
    /// The public <see cref="ActivityReportSqlPersistenceManager"/> constructors build a Graph credential for each batch's
    /// Copilot file and meeting lookups, even for a batch that has nothing to look up. Without a client secret, as on a
    /// Dependabot or fork pull request, every save then fails with "'clientSecret' cannot be null or empty if no client
    /// certificate configured." (#860). A test of the SQL path uses this instead, and needs no secret. Copilot lookups are
    /// answered as Graph answers a file or meeting it can't find.
    /// </remarks>
    internal static class OfflineSqlActivityPersistence
    {
        internal static ActivityReportSqlPersistenceManager Create(ILogger logger, AppConfig config)
        {
            return new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), UserImportScope.Unfiltered, logger, config,
                maxConcurrentSaves: 1, usePerBatchDedupCache: false, clock: null, cacheProvider: null, stagingWriter: null,
                copilotMetadataLoaderFactory: new FakeCopilotMetadataLoaderFactory(new ReturnNullFilesAndMeetingsAdaptor()),
                saveSessionFactory: null);
        }
    }
}

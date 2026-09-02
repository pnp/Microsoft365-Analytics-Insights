using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// Builds the run-scoped Copilot Graph metadata loader. A port purely so that the audit-log persistence
    /// adapter no longer constructs a Graph client (and therefore no longer needs the network) to decide what
    /// to pre-warm - see issue #373, which calls this out explicitly.
    /// </summary>
    public interface ICopilotMetadataLoaderFactory
    {
        /// <summary>
        /// Authenticate and build a loader. Throwing is a legitimate outcome (no Graph credentials, for
        /// instance); <see cref="CopilotMetadataPrewarmer"/> owns the best-effort handling of that.
        /// </summary>
        Task<ICopilotMetadataLoader> CreateAsync();
    }

    /// <summary>
    /// Production <see cref="ICopilotMetadataLoaderFactory"/> - the app-identity Graph client the persistence
    /// adapter used to build inline.
    /// </summary>
    public sealed class GraphCopilotMetadataLoaderFactory : ICopilotMetadataLoaderFactory
    {
        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;

        public GraphCopilotMetadataLoaderFactory(ILogger logger, AppConfig appConfig)
        {
            _logger = logger;
            _appConfig = appConfig;
        }

        public async Task<ICopilotMetadataLoader> CreateAsync()
        {
            var auth = new GraphAppIndentityOAuthContext(_logger, _appConfig.ClientID, _appConfig.TenantGUID.ToString(), _appConfig.ClientSecret, _appConfig.KeyVaultUrl, _appConfig.UseClientCertificate);
            await auth.InitClientCredential();
            return new GraphFileMetadataLoader(new GraphServiceClient(auth.Creds), _logger);
        }
    }

    /// <summary>
    /// Owns the run-scoped Copilot metadata loader and the pre-warm pass that fills its Graph caches before
    /// the save takes the single-permit SQL lock. Lifted out of <c>ActivityReportSqlPersistenceManager</c> by
    /// issue #373 so both halves can be exercised with no Graph and no SQL Server.
    ///
    /// Why the pre-warm exists: the per-event <c>ProcessExtendedProperties</c> pass runs serially inside the
    /// SQL lock and, for Copilot file events, calls Graph. Resolving those contexts ahead of time - across
    /// batches and concurrently within a batch - turns the in-lock calls into cache hits, so the lock is held
    /// for SQL work rather than network round-trips.
    ///
    /// One instance per import cycle: the loader is built at most once and its caches (resolved files, users,
    /// sites and unresolvable contexts) then persist for the whole import instead of being rebuilt per batch.
    /// </summary>
    public sealed class CopilotMetadataPrewarmer
    {
        /// <summary>
        /// How many Copilot file contexts to resolve concurrently while pre-warming (outside the SQL lock).
        /// </summary>
        public const int PrewarmConcurrency = 8;

        private readonly ICopilotMetadataLoaderFactory _loaderFactory;
        private readonly ILogger _logger;

        private ICopilotMetadataLoader _sharedCopilotLoader;
        private bool _sharedCopilotLoaderTried;
        private readonly SemaphoreSlim _sharedLoaderInitLock = new SemaphoreSlim(1, 1);

        public CopilotMetadataPrewarmer(ICopilotMetadataLoaderFactory loaderFactory, ILogger logger)
        {
            if (loaderFactory == null) throw new ArgumentNullException(nameof(loaderFactory));
            _loaderFactory = loaderFactory;
            _logger = logger;
        }

        /// <summary>
        /// Get the run-scoped loader and, when <see cref="CopilotPrewarmPolicy.ShouldPrewarm"/> says so,
        /// pre-resolve this batch's Copilot file contexts into it. Returns the loader (possibly null) for the
        /// save path to hand to its <c>SaveSession</c>.
        /// </summary>
        /// <param name="resolveCopilotResourceMetadata">
        /// The tenant's <c>ResolveCopilotResourceMetadata</c> setting, read per batch exactly as before. With
        /// it off the save path makes no Graph resource calls at all, so warming would be pure outbound Graph
        /// traffic for a cache nothing reads.
        /// </param>
        public async Task<ICopilotMetadataLoader> GetLoaderAndPrewarmAsync(IEnumerable<AbstractAuditLogContent> activities, bool resolveCopilotResourceMetadata)
        {
            var sharedLoader = await GetSharedLoaderAsync();
            if (CopilotPrewarmPolicy.ShouldPrewarm(sharedLoader != null, resolveCopilotResourceMetadata))
            {
                await PrewarmFileMetadataAsync(activities, sharedLoader);
            }
            return sharedLoader;
        }

        /// <summary>
        /// Lazily build the run-scoped loader (once). Best-effort: on any failure (e.g. no Graph credentials
        /// in a test) returns null and callers fall back to the per-session loader. Thread-safe.
        /// </summary>
        private async Task<ICopilotMetadataLoader> GetSharedLoaderAsync()
        {
            if (_sharedCopilotLoaderTried)
            {
                return _sharedCopilotLoader;
            }
            await _sharedLoaderInitLock.WaitAsync();
            try
            {
                if (!_sharedCopilotLoaderTried)
                {
                    try
                    {
                        _sharedCopilotLoader = await _loaderFactory.CreateAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not build a run-scoped Copilot metadata loader; falling back to per-batch loaders.");
                        _sharedCopilotLoader = null;
                    }
                    _sharedCopilotLoaderTried = true;
                }
            }
            finally
            {
                _sharedLoaderInitLock.Release();
            }
            return _sharedCopilotLoader;
        }

        /// <summary>
        /// Resolve the file metadata for this batch's Copilot file contexts concurrently, warming the shared
        /// loader's cache. Errors are swallowed - the authoritative resolution + logging happens in the serial
        /// ProcessExtendedProperties pass (this only pre-populates the cache).
        /// </summary>
        private async Task PrewarmFileMetadataAsync(IEnumerable<AbstractAuditLogContent> activities, ICopilotMetadataLoader loader)
        {
            var fileContexts = CopilotPrewarmPolicy.ExtractFileContexts(activities);
            if (fileContexts.Count == 0) return;

            using (var throttle = new SemaphoreSlim(PrewarmConcurrency))
            {
                var tasks = fileContexts.Select(async kvp =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        await loader.GetSpoFileInfo(kvp.Key, kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Copilot metadata prewarm failed for context {ctx} (will retry in the serial pass)", kvp.Key);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
        }
    }
}

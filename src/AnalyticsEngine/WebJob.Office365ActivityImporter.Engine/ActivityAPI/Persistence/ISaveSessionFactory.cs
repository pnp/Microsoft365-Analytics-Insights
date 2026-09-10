using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// Creates the <see cref="SaveSession"/> used by a save batch's metadata pass, replacing the
    /// <c>new SaveSession(...)</c> + <c>Init()</c> pair that <c>ActivityReportSqlPersistenceManager</c> did
    /// inline (issue #373).
    ///
    /// This is a real seam, not bookkeeping: <c>SaveSession.Init()</c> constructs a
    /// <c>CopilotAuditEventManager</c> and a <c>PowerPlatformAuditEventManager</c> from the configured
    /// connection string and - when no shared loader is supplied - authenticates a Graph client. Putting it
    /// behind a port is what lets a later change substitute the metadata pass's collaborators.
    /// </summary>
    public interface ISaveSessionFactory
    {
        /// <summary>
        /// Build and initialise a session bound to <paramref name="db"/>.
        /// </summary>
        /// <param name="sharedCopilotLoader">
        /// The run-scoped Copilot metadata loader, so per-event Graph resolution hits the cache warmed
        /// outside the SQL lock. Null makes the session build its own (the original behaviour).
        /// </param>
        Task<SaveSession> CreateAsync(AnalyticsEntitiesContext db, ICopilotMetadataLoader sharedCopilotLoader);
    }

    /// <summary>
    /// Production <see cref="ISaveSessionFactory"/>.
    /// </summary>
    public sealed class SaveSessionFactory : ISaveSessionFactory
    {
        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;

        public SaveSessionFactory(ILogger logger, AppConfig appConfig)
        {
            _logger = logger;
            _appConfig = appConfig;
        }

        public async Task<SaveSession> CreateAsync(AnalyticsEntitiesContext db, ICopilotMetadataLoader sharedCopilotLoader)
        {
            // Deliberately not disposed here or by the caller: SaveSession.Dispose() disposes the EF context,
            // which the save path owns and disposes itself. That is the pre-#373 behaviour.
            var saveSession = new SaveSession(_logger, db, _appConfig, sharedCopilotLoader);
            await saveSession.Init();
            return saveSession;
        }
    }
}

using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Graph implementation of <see cref="ICallRecordSourceLoader"/>. See issue #378.
    /// </summary>
    public class GraphCallRecordSourceLoader : ICallRecordSourceLoader
    {
        private readonly ManualGraphCallClient _graphCallClient;
        private readonly TeamsLoadContext _teamsLoadContext;
        private readonly ILogger _logger;
        private readonly string _thisTenantId;

        public GraphCallRecordSourceLoader(ManualGraphCallClient graphCallClient, TeamsLoadContext teamsLoadContext, ILogger logger, string thisTenantId)
        {
            _graphCallClient = graphCallClient ?? throw new ArgumentNullException(nameof(graphCallClient));
            _teamsLoadContext = teamsLoadContext ?? throw new ArgumentNullException(nameof(teamsLoadContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _thisTenantId = thisTenantId;
        }

        public Task<CallRecordDTO> LoadCallRecord(string callId)
        {
            return CallRecordDTO.LoadFromGraphByID(callId, _graphCallClient, _teamsLoadContext, _logger, _thisTenantId);
        }
    }

    /// <summary>
    /// SQL implementation of <see cref="ICallRecordPersistenceManager"/>, opening its own
    /// <see cref="AnalyticsEntitiesContext"/> per call record - which is the scope the save has always
    /// run in. See issue #378.
    /// </summary>
    public class SqlCallRecordPersistenceManager : ICallRecordPersistenceManager
    {
        private readonly ILogger _logger;

        public SqlCallRecordPersistenceManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SaveOrReplaceCallRecord(CallRecordDTO call)
        {
            if (call is null) throw new ArgumentNullException(nameof(call));

            using (var db = new AnalyticsEntitiesContext())
            {
                await call.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), _logger);
            }
        }
    }
}

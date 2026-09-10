using ActivityImporter.Engine.ActivityAPI.Copilot;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ICopilotStagingWriter"/>. Exposes the staged rows so the manager's
    /// adaptation rules - context priority, agent-metadata-only mode, the null guard - can be asserted
    /// with no SQL Server. See issue #367.
    /// </summary>
    internal class InMemoryCopilotStagingWriter : ICopilotStagingWriter
    {
        public List<SPCopilotLogTempEntity> SharePointRows { get; } = new List<SPCopilotLogTempEntity>();
        public List<TeamsCopilotLogTempEntity> TeamsRows { get; } = new List<TeamsCopilotLogTempEntity>();
        public List<ChatOnlyCopilotLogTempEntity> ChatOnlyRows { get; } = new List<ChatOnlyCopilotLogTempEntity>();

        /// <summary>How many times the batch was committed.</summary>
        public int CommitCount { get; private set; }

        /// <summary>Total rows staged across all three tables.</summary>
        public int TotalStaged => SharePointRows.Count + TeamsRows.Count + ChatOnlyRows.Count;

        public void StageSharePoint(SPCopilotLogTempEntity row) => SharePointRows.Add(row);

        public void StageTeams(TeamsCopilotLogTempEntity row) => TeamsRows.Add(row);

        public void StageChatOnly(ChatOnlyCopilotLogTempEntity row) => ChatOnlyRows.Add(row);

        public Task<CopilotStagingCommitTimings> CommitAllChanges()
        {
            CommitCount++;
            // The SQL adapter clears its staged rows on commit; mirror that so a test can assert the
            // second batch independently of the first.
            SharePointRows.Clear();
            TeamsRows.Clear();
            ChatOnlyRows.Clear();
            return Task.FromResult(new CopilotStagingCommitTimings());
        }
    }

    /// <summary>
    /// In-memory <see cref="IPowerPlatformStagingWriter"/>. See issue #367.
    /// </summary>
    internal class InMemoryPowerPlatformStagingWriter : IPowerPlatformStagingWriter
    {
        public List<PowerAppLogTempEntity> PowerAppRows { get; } = new List<PowerAppLogTempEntity>();
        public List<PowerAppShareLogTempEntity> PowerAppShareRows { get; } = new List<PowerAppShareLogTempEntity>();
        public List<PowerAutomateFlowLogTempEntity> FlowRows { get; } = new List<PowerAutomateFlowLogTempEntity>();
        public List<PowerAutomateFlowShareLogTempEntity> FlowShareRows { get; } = new List<PowerAutomateFlowShareLogTempEntity>();
        public List<PowerBILogTempEntity> PowerBiRows { get; } = new List<PowerBILogTempEntity>();
        public List<CopilotStudioLogTempEntity> CopilotStudioRows { get; } = new List<CopilotStudioLogTempEntity>();

        public int CommitCount { get; private set; }

        public void StagePowerApp(PowerAppLogTempEntity row) => PowerAppRows.Add(row);

        public void StagePowerAppShare(PowerAppShareLogTempEntity row) => PowerAppShareRows.Add(row);

        public void StageFlow(PowerAutomateFlowLogTempEntity row) => FlowRows.Add(row);

        public void StageFlowShare(PowerAutomateFlowShareLogTempEntity row) => FlowShareRows.Add(row);

        public void StagePowerBi(PowerBILogTempEntity row) => PowerBiRows.Add(row);

        public void StageCopilotStudio(CopilotStudioLogTempEntity row) => CopilotStudioRows.Add(row);

        public Task CommitAllChanges()
        {
            CommitCount++;
            PowerAppRows.Clear();
            PowerAppShareRows.Clear();
            FlowRows.Clear();
            FlowShareRows.Clear();
            PowerBiRows.Clear();
            CopilotStudioRows.Clear();
            return Task.CompletedTask;
        }
    }
}

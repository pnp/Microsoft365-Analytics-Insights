using Common.Entities;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Sections
{
    /// <summary>
    /// One independently-selectable Graph import section (user metadata, usage reports, Teams, sent emails,
    /// Copilot usage reports, Copilot interaction history).
    ///
    /// This is the seam that separates <b>composition</b> from <b>orchestration</b> (issue #376):
    /// <see cref="GraphImporter"/> knows only how to select, gate and run sections, while everything about
    /// how a section is built - Graph clients, delta-token stores, DB contexts - lives behind
    /// <see cref="IGraphImportSectionFactory"/>. The orchestration loop is then testable with fake sections
    /// and no SQL Server, Graph, Redis or Service Bus.
    ///
    /// A section is expected to be <b>cheap to construct</b>: everything it needs is built inside
    /// <see cref="RunAsync"/>, so a section that is disabled or gated off this cycle costs nothing. That
    /// matters beyond tidiness - the sent-email section opens a Redis connection while building its delta
    /// token store, which must not happen on a cycle where the section does not run.
    /// </summary>
    public interface IGraphImportSection
    {
        /// <summary>
        /// Operator-facing name, used as the <see cref="DataUtils.JobTimer"/> operation name and in the
        /// cadence-gate log lines.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Logged verbatim (at information level) when <see cref="IsEnabled"/> is false.
        /// Kept per-section because the existing messages are not derivable from <see cref="Name"/>.
        /// </summary>
        string DisabledMessage { get; }

        /// <summary>
        /// The <see cref="IImportLastRunStore"/> key used to gate this section to at most once per
        /// <see cref="IntervalHours"/>, or <c>null</c> for a section that is not cadence-gated (it either
        /// runs every cycle or owns its own throttle, as the activity/usage-report phase does).
        /// </summary>
        string CadenceKey { get; }

        /// <summary>
        /// Minimum hours between runs. <c>0</c> disables gating. Ignored when <see cref="CadenceKey"/> is null.
        /// </summary>
        int IntervalHours { get; }

        /// <summary>
        /// Whether the tenant has this import switched on.
        /// </summary>
        bool IsEnabled(ImportTaskSettings settings);

        /// <summary>
        /// Runs the section. Returns false when the section did not complete - the orchestrator then neither
        /// stamps the cadence gate nor emits a "finished section" event, so the section retries next cycle.
        /// A section that throws is NOT isolated: the exception unwinds out of
        /// <c>GraphImporter.GetAndSaveAllGraphData</c> and the sections after it are skipped for this cycle.
        /// That is the pre-existing behaviour and is deliberately left unchanged here.
        /// </summary>
        Task<bool> RunAsync();
    }
}

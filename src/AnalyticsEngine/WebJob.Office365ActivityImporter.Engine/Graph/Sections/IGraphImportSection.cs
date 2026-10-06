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
    /// and no SQL Server, Graph, Azure Storage or Service Bus.
    ///
    /// A section is expected to be <b>cheap to construct</b>: everything it needs is built inside
    /// <see cref="RunAsync"/>, so a section that is disabled or gated off this cycle costs nothing. That
    /// matters beyond tidiness - the sent-email section opens the runtime state table while building its delta
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
        /// True for a section that runs in the <b>deferred pass</b>, <c>GraphImporter.GetAndSaveDeferredGraphData</c>,
        /// instead of the main pass, <c>GraphImporter.GetAndSaveNonDeferredGraphData</c>. The WebJob starts the deferred
        /// pass in the background once a cycle's main pass and audit import are done, and does not wait for it, so a
        /// slow section here cannot hold back the near-real-time audit data (Copilot, Power Platform, DLP and SharePoint
        /// audit) of this or any later cycle. Today that is only the once-a-day usage-report phase, which can take hours
        /// on a large tenant (issue #706).
        ///
        /// Deferral changes only <b>when</b> a section runs. It is selected, cadence-gated, timed and logged exactly as
        /// it would be in the main pass.
        /// </summary>
        bool IsDeferred { get; }

        /// <summary>
        /// Whether the tenant has this import switched on.
        /// </summary>
        bool IsEnabled(ImportTaskSettings settings);

        /// <summary>
        /// Runs the section. Returns false when the section did not complete - the orchestrator then neither
        /// stamps the cadence gate nor emits a "finished section" event, so the section retries next cycle.
        /// A section that throws is NOT isolated: the exception unwinds out of the <c>GraphImporter</c> pass
        /// that is running it, and the sections after it in that pass are skipped for this cycle. That is the
        /// pre-existing behaviour and is deliberately left unchanged here. The WebJob calls the main and the
        /// deferred pass separately, each in its own try/catch, so a throw in one pass never skips the other.
        /// </summary>
        Task<bool> RunAsync();
    }
}

using Common.Entities;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Sections
{
    /// <summary>
    /// An <see cref="IGraphImportSection"/> whose body is a delegate. The section bodies are lifted verbatim
    /// out of <c>GraphImporter.GetAndSaveAllGraphData</c>, so keeping them as lambdas in
    /// <see cref="ProductionGraphImportSectionFactory"/> makes this a pure move rather than six new classes
    /// that would each need their own review against the original.
    ///
    /// Construct via <see cref="Gated"/> / <see cref="Ungated"/> rather than the constructor, so the caller
    /// cannot accidentally build a gated section with no cadence key (or an ungated one with a key that would
    /// then never be read).
    /// </summary>
    public sealed class DelegateGraphImportSection : IGraphImportSection
    {
        private readonly Func<ImportTaskSettings, bool> _isEnabled;
        private readonly Func<Task<bool>> _run;

        private DelegateGraphImportSection(string name, string disabledMessage, string cadenceKey, int intervalHours,
            Func<ImportTaskSettings, bool> isEnabled, Func<Task<bool>> run, bool isDeferred = false)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A section needs a name.", nameof(name));
            if (string.IsNullOrWhiteSpace(disabledMessage)) throw new ArgumentException("A section needs a disabled message.", nameof(disabledMessage));

            Name = name;
            DisabledMessage = disabledMessage;
            CadenceKey = cadenceKey;
            IntervalHours = intervalHours;
            IsDeferred = isDeferred;
            _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
            _run = run ?? throw new ArgumentNullException(nameof(run));
        }

        /// <summary>
        /// A section gated to at most once per <paramref name="intervalHours"/> via
        /// <see cref="IImportLastRunStore"/>.
        /// </summary>
        public static DelegateGraphImportSection Gated(string name, string disabledMessage, string cadenceKey,
            int intervalHours, Func<ImportTaskSettings, bool> isEnabled, Func<Task<bool>> run)
        {
            if (string.IsNullOrWhiteSpace(cadenceKey)) throw new ArgumentException("A gated section needs a cadence key.", nameof(cadenceKey));
            return new DelegateGraphImportSection(name, disabledMessage, cadenceKey, intervalHours, isEnabled, run);
        }

        /// <summary>
        /// A section that runs on every cycle it is enabled for, because it either has no throttle at all or
        /// owns one of its own (the activity/usage-report phase throttles itself via
        /// <see cref="ISingleDateStore"/>).
        /// </summary>
        public static DelegateGraphImportSection Ungated(string name, string disabledMessage,
            Func<ImportTaskSettings, bool> isEnabled, Func<Task<bool>> run)
        {
            return new DelegateGraphImportSection(name, disabledMessage, null, 0, isEnabled, run);
        }

        /// <summary>
        /// This section, moved to the deferred pass that the WebJob runs in the background (see
        /// <see cref="IGraphImportSection.IsDeferred"/>). Everything else about it - name, message, gating and body -
        /// is unchanged. A method rather than another optional parameter on <see cref="Gated"/> /
        /// <see cref="Ungated"/>, because a trailing optional argument is baked in by the calling compiler and so is
        /// binary-breaking for already-compiled callers.
        /// </summary>
        public DelegateGraphImportSection Deferred()
        {
            return new DelegateGraphImportSection(Name, DisabledMessage, CadenceKey, IntervalHours, _isEnabled, _run, isDeferred: true);
        }

        public string Name { get; }
        public string DisabledMessage { get; }
        public string CadenceKey { get; }
        public int IntervalHours { get; }
        public bool IsDeferred { get; }

        public bool IsEnabled(ImportTaskSettings settings) => _isEnabled(settings);

        public Task<bool> RunAsync() => _run();
    }
}

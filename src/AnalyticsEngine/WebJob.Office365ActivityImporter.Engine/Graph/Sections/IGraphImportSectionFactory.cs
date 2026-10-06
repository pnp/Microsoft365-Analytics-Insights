using Common.Entities.Config;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Sections
{
    /// <summary>
    /// Builds the ordered list of Graph import sections for one import cycle. This is the composition root
    /// for the Graph import: every <c>new</c> that <see cref="GraphImporter"/> used to do inline lives behind
    /// this interface (issue #376).
    ///
    /// The returned order is the order the sections run in within their pass, and it is significant - see
    /// <see cref="ProductionGraphImportSectionFactory.CreateSections"/>. Sections marked
    /// <see cref="IGraphImportSection.IsDeferred"/> run in a later pass than the rest (issue #706).
    /// </summary>
    public interface IGraphImportSectionFactory
    {
        /// <param name="settings">
        /// The settings for this cycle, as passed to the <c>GraphImporter</c> pass being run
        /// (<c>GetAndSaveAllGraphData</c>, <c>GetAndSaveNonDeferredGraphData</c> or <c>GetAndSaveDeferredGraphData</c>).
        /// </param>
        IReadOnlyList<IGraphImportSection> CreateSections(AppConfig settings);
    }
}

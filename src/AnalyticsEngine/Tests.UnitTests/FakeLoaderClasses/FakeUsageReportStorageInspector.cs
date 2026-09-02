using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IUsageReportStorageInspector"/> - the two raw <c>sys.indexes</c> queries the
    /// daily usage-report loaders used to issue inline, replaced by a flag and a counter. See issue #375.
    /// </summary>
    public class FakeUsageReportStorageInspector : IUsageReportStorageInspector
    {
        public FakeUsageReportStorageInspector(bool hasLeadingDateIndex = true)
        {
            HasLeadingDateIndex = hasLeadingDateIndex;
        }

        /// <summary>What <see cref="HasLeadingDateIndexAsync"/> answers.</summary>
        public bool HasLeadingDateIndex { get; set; }

        /// <summary>Table names the index question was asked about, in order.</summary>
        public List<string> IndexQuestionsAsked { get; } = new List<string>();

        /// <summary>Table names compaction was requested for, in order.</summary>
        public List<string> CompactionsRequested { get; } = new List<string>();

        public Task<bool> HasLeadingDateIndexAsync(string qualifiedTableName)
        {
            IndexQuestionsAsked.Add(qualifiedTableName);
            return Task.FromResult(HasLeadingDateIndex);
        }

        public Task CompactColumnstoreAsync(string qualifiedTableName)
        {
            CompactionsRequested.Add(qualifiedTableName);
            return Task.CompletedTask;
        }
    }
}

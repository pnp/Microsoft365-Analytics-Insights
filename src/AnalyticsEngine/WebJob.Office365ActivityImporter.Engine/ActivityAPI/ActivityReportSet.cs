using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    /// <summary>
    /// One or more audit log reports. Usually loaded from the API (in WebActivityReportSet)
    /// </summary>
    public abstract class ActivityReportSet : List<AbstractAuditLogContent>
    {
        #region Constructors

        public ActivityReportSet() : this(0) { }
        public ActivityReportSet(int capacity) : base(capacity) { }

        public ActivityReportSet(IEnumerable<AbstractAuditLogContent> collection) : base(collection)
        {
        }
        #endregion

        #region Props

        /// <summary>
        /// From all the reports, the earliest date-time for created
        /// </summary>
        public DateTime OldestContent
        {
            get
            {
                // Single O(n) pass with no allocations, vs. the prior OrderByDescending().Last()
                // which performed a full O(n log n) sort just to pick the min.
                return this.Min(c => c.CreationTime);
            }
        }

        /// <summary>
        /// From all the reports, the latest date-time for created
        /// </summary>
        public DateTime NewestContent
        {
            get
            {
                return this.Max(c => c.CreationTime);
            }
        }

        #endregion

        /// <summary>
        /// Write all to SQL with a new data cache for the events only in this content-set
        /// </summary>
        public async Task<ImportStat> CommitAllToSQL(IActivityReportPersistenceManager persistenceManager)
        {
            return await persistenceManager.CommitAll(this);
        }

    }
}

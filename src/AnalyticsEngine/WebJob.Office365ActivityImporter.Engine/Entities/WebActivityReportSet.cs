using System.Collections.Generic;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{


    /// <summary>
    /// List of content loaded from the Activity API specifically
    /// </summary>
    public class WebActivityReportSet : ActivityReportSet
    {
        #region Constructors

        /// <summary>
        /// Constructor for JSon deserialisation 
        /// </summary>
        public WebActivityReportSet()
        {
        }
        public WebActivityReportSet(ActivityReportInfo metaData) : this()
        {
            this.OriginalMetadata = metaData;
        }
        public WebActivityReportSet(int capacity) : base(capacity) { }

        public WebActivityReportSet(IEnumerable<AbstractAuditLogContent> collection) : base(collection)
        {
        }

        #endregion

        /// <summary>
        /// Original metadata used to populate content-set for
        /// </summary>
        public ActivityReportInfo OriginalMetadata { get; set; }

    }

}

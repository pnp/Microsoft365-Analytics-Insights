using Microsoft.Graph;
using System;
using System.Collections.Generic;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    /// <summary>
    /// Stats for work done on a batch
    /// </summary>
    public class ImportStat
    {
        public int Imported { get; set; }
        public int ProcessedAlready { get; set; }
        public int URLsOutOfScope { get; set; }
        public int DownloadErrors { get; set; }
        public int Total { get; set; }

        public List<TimePeriod> ForTimeSlots { get; set; }

        public void AddStats(ImportStat statsToAdd)
        {
            if (statsToAdd == null)
            {
                throw new ArgumentNullException("statsToAdd");
            }
            this.ProcessedAlready += statsToAdd.ProcessedAlready;
            this.Imported += statsToAdd.Imported;
            this.URLsOutOfScope += statsToAdd.URLsOutOfScope;
            this.DownloadErrors += statsToAdd.DownloadErrors;
            this.Total += statsToAdd.Total;
        }

        public override string ToString()
        {
            return
                $"Imported successfully: {this.Imported.ToString("n0")}, " +
                $"already processed: {this.ProcessedAlready.ToString("n0")}, " +
                $"URLs out of scope (orgs table): {this.URLsOutOfScope.ToString("n0")}, " +
                $"errors: {this.DownloadErrors.ToString("n0")}, " +
                $"total: {this.Total.ToString("n0")}";
        }
    }
}

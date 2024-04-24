using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.DataTypes;
using WebJob.AppInsightsImporter.Engine.Properties;

namespace WebJob.AppInsightsImporter.Engine
{

    /// <summary>
    /// An update to an existing hit in the DB. For time-on-page.
    /// </summary>
    public class HitPatch
    {
        public HitPatch() { }

        /// <summary>
        /// Create patch from CustomEvent
        /// </summary>
        public HitPatch(CustomEvent import) : this()
        {
            this.PageRequestID = Guid.Parse(import.Context.CustomProperties.PageRequestId);
            this.TimeOnPage = Math.Round(import.Context.CustomProperties.ActiveTime, 2);
        }

        public Guid PageRequestID { get; set; }

        public double TimeOnPage { get; set; }

        public override string ToString()
        {
            return $"PageRequestID: '{PageRequestID}', TimeOnPage: '{TimeOnPage}'";
        }
    }

    /// <summary>
    /// A list of changes to make to various existing hits
    /// </summary>
    public class HitUpdateList : List<HitPatch>
    {
        public async Task ApplyChanges()
        {
            if (this.Count == 0)
            {
                return;
            }


            SPOInsights.Common.DataUtils.ConsoleApp.WriteLine($"Merging {this.Count.ToString("n0")} updates...");

            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();
            using (SPOInsightsEntitiesContext db = new SPOInsightsEntitiesContext())
            {
                // Build staging table
                db.Database.ExecuteSqlCommand(Resources.Create_Update_Temp_Table);
                InsertUpdatesIntoStaging(db.Database);

                // Perform update
                //Console.WriteLine($"Merging updates...");
                await db.Database.ExecuteSqlCommandAsync(Resources.Update_Hits_From_Staging);
            }

            stopwatch.Stop();
            TimeSpan timeTaken = TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds);
            //SPOInsights.Common.DataUtils.ConsoleApp.WriteLine($"Patching done in {timeTaken.Minutes} minutes, {timeTaken.Seconds} seconds.");
        }

        private void InsertUpdatesIntoStaging(Database database)
        {
            // Populate staging table
            int i = 0;
            Console.WriteLine($"Inserting {this.Count.ToString("n0")} updates to staging table...");
            foreach (var p in this)
            {
                string sqlInsert = string.Empty;

                // We need to patch a broken hit with URL & Page-title
                sqlInsert = Environment.NewLine + "insert into [hit_updates] (" +
                    "[page_request_id], " +
                    "[seconds_on_page]" +
                    ") " +
                    $"values (@p0,@p1)";

                database.ExecuteSqlCommand(sqlInsert,
                    p.PageRequestID,
                    p.TimeOnPage
                );
                
                if (i % 100 == 0)
                {
                    Console.Write($"{i}...");
                }
                
                i++;
            }
        }
    }
}

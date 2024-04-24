using System;
using System.Linq;

namespace WebJob.AppInsightsImporter
{
    internal class AuditLogParser
    {
        /// <summary>
        /// Import Activity API staging logs
        /// </summary>
        internal static void ProcessStagingLogs()
        {
            using (var entities1 = new SPOInsightsEntities())
            {

                var allDownloadAuditLogs = from downloadAuditLogs in entities1.auditsharepoints
                                           where downloadAuditLogs.Operation.StartsWith("FileDownloaded")
                                           select downloadAuditLogs;

                int downloadCount = 0;

                foreach (auditsharepoint downloadAutitItem in allDownloadAuditLogs)
                {
                    downloadCount++;
                    download d = new download();

                    Uri downloadUri = new Uri(downloadAutitItem.ObjectId);

                    var urlRecord = DataHelper.GetUrl(downloadUri.AbsoluteUri, entities1);
                    d.url = urlRecord;
                    d.download_timestamp = downloadAutitItem.CreationTime.Value;

                    // User lookup
                    var userRecords = from allUrls in entities1.users
                                      where allUrls.user_name == downloadAutitItem.UserId
                                      select allUrls;
                    user userRecord = null;
                    if (userRecords != null && userRecords.Any())
                    {
                        userRecord = userRecords.First();
                    }
                    else
                    {
                        userRecord = new user();
                        userRecord.user_name = downloadAutitItem.UserId;
                    }

                    d.user = userRecord;

                    // Add new
                    entities1.downloads.Add(d);

                    // Remove old
                    // entities1.auditsharepoints.Remove(downloadAutitItem);
                }
                entities1.Database.Log = s => System.Diagnostics.Debug.WriteLine(s);
                Console.WriteLine();
                SPOInsights.Common.DataUtils.ConsoleApp.WriteLine("{0} Office 365 audit entries imported from staging area.", downloadCount);
                entities1.SaveChanges();
            }
        }
    }
}

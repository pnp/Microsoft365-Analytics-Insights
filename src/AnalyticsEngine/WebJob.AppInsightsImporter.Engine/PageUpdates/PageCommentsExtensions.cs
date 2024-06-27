using Common.Entities;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.Properties;

namespace WebJob.AppInsightsImporter.Engine.PageUpdates
{
    public static class PageCommentsExtensions
    {
        public static async Task Save(this List<PageCommentTemp> comments, AnalyticsEntitiesContext db, ILogger debugTracer)
        {
            var commentsToInsert = new EFInsertBatch<PageCommentTemp>(db, debugTracer);
            commentsToInsert.Rows.AddRange(comments);

            var mergeSql = Resources.Migrate_New_Comments.Replace("${STAGING_TABLE_COMMENTS}", PageCommentTemp.STAGING_TABLENAME);
            await commentsToInsert.SaveToStagingTable(mergeSql);
        }
    }

    [TempTableName(STAGING_TABLENAME)]
    public class PageCommentTemp
    {

#if !DEBUG
        public const string STAGING_TABLENAME = "##import_staging_comments";
#else
        public const string STAGING_TABLENAME = "debug_staging_comments";
#endif

        public PageCommentTemp(string comment, DateTime created, int userId, int spId, int urlId, int? parentSpId)
        {
            this.Comment = comment;
            this.Created = created;
            this.UserID = userId;
            this.SpID = spId;
            this.UrlID = urlId;
            this.ParentCommentSharePointId = parentSpId;
        }

        [Column("comment")]
        public string Comment { get; set; }

        [Column("language_id")]
        public int? LanguageID { get; set; }

        [Column("user_id")]
        public int UserID { get; set; }

        [Column("sp_id")]
        public int SpID { get; set; }

        [Column("created")]
        public DateTime Created { get; set; }

        [Column("url_id")]
        public int UrlID { get; set; }

        [Column("sentiment_score")]
        public double? SentimentScore { get; set; }

        [Column("ParentCommentSharePointId")]
        public int? ParentCommentSharePointId { get; set; }
    }

}

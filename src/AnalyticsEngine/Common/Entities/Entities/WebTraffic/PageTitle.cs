
using Common.Entities.Entities;
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Titles lookup used in a hit.
    /// </summary>
    [Table("page_titles")]
    public class PageTitle : AbstractEFEntity
    {
        public string title { get; set; }

    }

    public abstract class SPUrlUserRecord : AbstractEFEntity
    {
        [Column("user_id")]
        [ForeignKey(nameof(User))]
        public int UserID { get; set; }
        public User User { get; set; }


        [Column("url_id")]
        [ForeignKey(nameof(Url))]
        public int UrlID { get; set; }
        public Url Url { get; set; }

        [Column("created")]
        public DateTime Created { get; set; }

        [Column("sp_id")]
        public int SpID { get; set; }
    }

    [Table("page_likes")]
    public class PageLike : SPUrlUserRecord
    {
    }

    [Table("page_comments")]
    public class PageComment : SPUrlUserRecord
    {
        [Column("comment")]
        public string Comment { get; set; }


        [ForeignKey(nameof(Language))]
        [Column("language_id")]
        public int? LanguageID { get; set; }
        public Language Language { get; set; }

        [Column("sentiment_score")]
        public double? SentimentScore { get; set; }


        [Column("parent_id")]
        [ForeignKey(nameof(ParentComment))]
        public int? ParentCommentID { get; set; }
        public PageComment ParentComment { get; set; }

    }
}

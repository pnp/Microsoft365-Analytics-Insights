using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.WebTraffic
{
    /// <summary>
    /// Something clicked on in a pageview
    /// </summary>
    [Table("hits_clicked_elements")]
    public class Clicks : AbstractEFEntity
    {
        [ForeignKey(nameof(Url))]
        [Column("url_id")]
        public int? UrlId { get; set; }
        public Url Url { get; set; }

        public ClickedElementTitle Title { get; set; }
        [Column("element_title_id")]
        [ForeignKey(nameof(Title))]
        public int? ClickedElementTitleId { get; set; }

        public ClickedElementsClassNames ClassNames { get; set; }

        [Column("class_names_id")]
        [ForeignKey(nameof(ClassNames))]
        public int? ClassNamesID { get; set; }

        // Required prop
        [Column("hit_id")]
        [ForeignKey(nameof(PageView))]
        public int HitID { get; set; }
        public Hit PageView { get; set; }

        [Column("timestamp")]
        public DateTime TimeStamp { get; set; } = DateTime.MinValue;
    }

    [Table("hits_clicked_element_titles")]
    public class ClickedElementTitle : AbstractEFEntityWithName
    {
    }

    /// <summary>
    /// Single record for all CSS class names, "header style1" in a single label
    /// </summary>
    [Table("hits_clicked_element_class_names")]
    public class ClickedElementsClassNames : AbstractEFEntity
    {
        [MaxLength(2000)]
        [Column("class_names")]
        public string AllClassNames { get; set; }
    }
}

using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    [Table("group_crawl_config")]
    public class GroupsCrawlConfig : AbstractEFEntity
    {
        [Column("rule_graph_id")]
        public string TeamGraphId { get; set; }

        [Column("include")]
        public bool Include { get; set; }
    }

}

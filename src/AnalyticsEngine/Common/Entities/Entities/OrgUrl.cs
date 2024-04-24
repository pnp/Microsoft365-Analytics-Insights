
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// URL used for importing activity for from O365. 
    /// </summary>
    [Table("org_urls")]
    public class OrgUrl : AbstractEFEntity
    {
        [Column("url_base")]
        public string UrlBase { get; set; }

        [Column("exact_match")]
        public bool? ExactMatch { get; set; } = false;
    }
}

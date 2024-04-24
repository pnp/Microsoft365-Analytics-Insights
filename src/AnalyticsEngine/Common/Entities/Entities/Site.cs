using Common.DataUtils;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    [Table("sites")]
    public class Site : AbstractEFEntity, IUrlObject
    {
        public Site()
        {
            this.webs = new List<Web>();
        }

        [Column("url_base")]
        [MaxLength(500)]
        public string UrlBase { get; set; }

        [Column("site_id")]
        [MaxLength(100)]
        public string SiteId { get; set; }

        public List<Web> webs { get; set; }

        public string Url => UrlBase;
    }
}

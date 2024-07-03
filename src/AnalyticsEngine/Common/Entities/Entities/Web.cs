using DataUtils;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    [Table("webs")]
    public class Web : AbstractEFEntity, IUrlObject
    {

        [MaxLength(500)]
        public string url_base { get; set; }

        public string title { get; set; }


        public int site_id { get; set; }

        [ForeignKey("site_id")]
        [Required]
        public Site site { get; set; }

        public string Url => url_base;
    }
}

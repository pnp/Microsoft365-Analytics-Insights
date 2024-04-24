using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Organisation. Used with org_urls to determine what SharePoint sites to import activity for. 
    /// </summary>
    /// 
    [Table("orgs")]
    public class Org : AbstractEFEntity
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        public Org()
        {
            this.org_urls = new HashSet<OrgUrl>();
        }

        public string org_name { get; set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public virtual ICollection<OrgUrl> org_urls { get; set; }

    }
}


using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{

    /// <summary>
    /// User browser for a page-view.
    /// </summary>
    /// 
    [Table("browsers")]
    public class Browser : AbstractEFEntity
    {
        public string browser_name { get; set; }

    }
}


using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Province lookup for a page-view. 
    /// </summary>
    [Table("provinces")]
    public class Province : AbstractEFEntity
    {
        public string province_name { get; set; }

    }
}

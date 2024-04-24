
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// City of a page-view.
    /// </summary>
    /// 
    [Table("cities")]
    public class City : AbstractEFEntity
    {
        public string city_name { get; set; }

    }
}

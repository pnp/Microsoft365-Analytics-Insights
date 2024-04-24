
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Country of a page-view.
    /// </summary>
    [Table("countries")]
    public class Country : AbstractEFEntity
    {
        public string country_name { get; set; }

    }
}


using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// OS used by user in a page-view.
    /// </summary>
    [Table("operating_systems")]
    public class OperatingSystem : AbstractEFEntity
    {
        public string os_name { get; set; }

    }
}

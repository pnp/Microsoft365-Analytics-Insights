using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Device (laptop/phone) of a page view. 
    /// </summary>
    [Table("devices")]
    public class Device : AbstractEFEntity
    {
        public string device_name { get; set; }

    }
}

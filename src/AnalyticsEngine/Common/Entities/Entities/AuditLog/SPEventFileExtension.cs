
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// File extension for an O365 audit event.
    /// </summary>
    /// 
    [Table("event_file_ext")]
    public class SPEventFileExtension
    {
        public SPEventFileExtension()
        {
        }

        public int id { get; set; }
        public string extension_name { get; set; }

    }
}

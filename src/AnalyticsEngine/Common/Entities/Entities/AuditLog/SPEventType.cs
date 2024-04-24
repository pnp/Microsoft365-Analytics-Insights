
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Type of object the event occured on (file, page, web, folder, etc). Only used for SharePoint events. 
    /// </summary>
    /// 
    [Table("event_types")]
    public class SPEventType
    {
        public SPEventType()
        {
        }

        public int id { get; set; }
        public string type_name { get; set; }
    }
}

using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{

    /// <summary>
    /// Debugging import log of a thing imported. For both page hits & O365 activity.
    /// </summary>
    /// 
    [Table("import_log")]
    public class ImportLog : AbstractEFEntity
    {
        public string import_message { get; set; }
        public string contents { get; set; }
        public string machine_name { get; set; }
        public DateTime time_stamp { get; set; }
        public int? hit_id { get; set; }
        public Guid? event_id { get; set; }
        public int? search_id { get; set; }
    }
}

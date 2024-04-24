using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// The file name involved in an audit event. Only used in SharePoint events. 
    /// </summary>
    /// 
    [Table("event_file_names")]
    public class SPEventFileName : AbstractEFEntity
    {
        [Column("file_name")]
        public string Name { get; set; }
    }
}

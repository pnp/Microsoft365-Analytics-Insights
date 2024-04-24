using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Config
{
    [Table("sys_configs")]
    public class ConfigState : AbstractEFEntity
    {

        [Column("config_json")]
        public string ConfigJson { get; set; }

        [Column("date_applied")]
        public DateTime DateApplied { get; set; }

        [Column("installed_by_username")]
        public string InstalledByUser { get; set; }

        [Column("messages")]
        public string Messages { get; set; }
    }
}

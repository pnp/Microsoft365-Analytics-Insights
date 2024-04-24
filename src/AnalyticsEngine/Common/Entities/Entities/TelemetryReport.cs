using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    [Table("sys_telemetry_reports")]
    public class TelemetryReport : AbstractEFEntity
    {
        [Column("submitted")]
        public DateTime ReportSubmitted { get; set; }

        [Column("report")]
        public string Report { get; set; }
    }
}

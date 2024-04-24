using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    [Table("call_failures")]
    public class CallFailureReasonLookup : CallLookupBaseEFEntity
    {

        [Column("reason")]
        public string Reason { get; set; }
        [Column("stage")]
        public string Stage { get; set; }
    }
}

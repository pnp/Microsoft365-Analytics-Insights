using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{

    [Table("call_session_call_modalities")]
    public class CallSessionModalityLookup : AbstractEFEntity
    {
        [Column("call_modality_id")]
        [ForeignKey(nameof(CallModality))]
        public int CallModalityID { get; set; }
        public CallModality CallModality { get; set; }


        [Column("call_session_id")]
        [ForeignKey(nameof(CallSession))]
        public int CallSessionID { get; set; }
        public CallSession CallSession { get; set; }

    }

    [Table("call_modalities")]
    public class CallModality : AbstractEFEntityWithName
    {
    }
}

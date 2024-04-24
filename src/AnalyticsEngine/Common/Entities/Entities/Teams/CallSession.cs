using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;


namespace Common.Entities.Entities.Teams
{
    [Table("call_sessions")]
    public class CallSession : AbstractEFEntity
    {
        public CallSession()
        {
            this.CallModalityLookups = new List<CallSessionModalityLookup>();
        }

        public User Attendee { get; set; }

        [ForeignKey(nameof(Attendee))]
        [Column("attendee_user_id")]
        public int AttendeeUserID { get; set; }


        [Column("start")]
        public DateTime Start { get; set; }

        [Column("end")]
        public DateTime End { get; set; }

        public CallRecord ParentRecord { get; set; }

        [ForeignKey(nameof(ParentRecord))]
        [Column("call_record_id")]
        public int CallRecordID { get; set; }

        public List<CallSessionModalityLookup> CallModalityLookups { get; set; }


        public CallSessionModalityLookup AddCallModality(CallModality callModality)
        {
            var l = new CallSessionModalityLookup() { CallSession = this, CallModality = callModality };
            this.CallModalityLookups.Add(l);
            return l;
        }

    }
}

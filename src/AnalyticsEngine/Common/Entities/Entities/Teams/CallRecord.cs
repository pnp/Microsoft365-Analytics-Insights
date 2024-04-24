using Common.Entities.Teams;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.Entities.Teams
{
    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-callrecord?view=graph-rest-beta
    [Table("call_records")]
    public class CallRecord : AbstractEFEntity, IGraphEntity
    {
        public CallRecord()
        {
            this.Sessions = new List<CallSession>();
        }

        #region Props

        [Column("organizer_id")]
        [ForeignKey(nameof(Organizer))]
        public int OrganizerID { get; set; }
        public User Organizer { get; set; }

        [Column("call_type_id")]
        [ForeignKey(nameof(CallType))]
        public int CallTypeID { get; set; }
        public CallType CallType { get; set; }

        [Column("graph_id")]
        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string GraphID { get; set; }


        [Column("start")]
        public DateTime StartDateTime { get; set; }

        [Column("end")]
        public DateTime EndDateTime { get; set; }

        public List<CallSession> Sessions { get; set; }

        #endregion


        public static async Task<CallRecord> LoadByGraphID(string graphCallID, AnalyticsEntitiesContext context)
        {
            var call = await context.CallRecords
                .Where(c => c.GraphID == graphCallID).SingleOrDefaultAsync();
            return call;
        }
        public async Task DeleteAll(AnalyticsEntitiesContext database)
        {
            var sessions = await database.CallSessions
                .Include(s => s.CallModalityLookups)
                .Where(s => s.ParentRecord.ID == this.ID).ToListAsync();

            var feedback = await database.CallFeedback
                .Where(f => f.Call.ID == this.ID).ToListAsync();
            database.CallFeedback.RemoveRange(feedback);


            var failures = await database.CallFailures
                .Where(f => f.Call.ID == this.ID).ToListAsync();

            database.CallFailures.RemoveRange(failures);

            foreach (var s in sessions)
            {
                database.CallModalityLookups.RemoveRange(s.CallModalityLookups);
                database.CallSessions.Remove(s);
            }

            database.CallRecords.Remove(this);

            await database.SaveChangesAsync();
        }
    }


    public abstract class CallLookupBaseEFEntity : AbstractEFEntity
    {

        [Column("call_id")]
        [ForeignKey(nameof(Call))]
        public int CallID { get; set; }
        public CallRecord Call { get; set; }
    }

    public abstract class CallLookupWithUserBaseEFEntity : CallLookupBaseEFEntity
    {
        [Column("user_id")]
        [ForeignKey(nameof(User))]
        public int UserID { get; set; }

        public User User { get; set; }
    }
}

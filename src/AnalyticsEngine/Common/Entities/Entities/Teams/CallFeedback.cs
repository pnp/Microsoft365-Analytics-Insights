using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{

    [Table("call_feedback")]
    public class CallFeedback : CallLookupWithUserBaseEFEntity
    {
        [Column("rating")]
        public string Rating { get; set; }
        [Column("text")]
        public string Text { get; set; }


    }
}

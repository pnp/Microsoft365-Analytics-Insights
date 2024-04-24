using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.Entities
{
    [Table("yammer_messages")]
    public class YammerMessage : AbstractEFEntity
    {
        public User Sender { get; set; }

        [ForeignKey(nameof(Sender))]
        [Column("sender_id")]
        public int SenderID { get; set; }

        [Column("created")]
        public DateTime Created { get; set; }

        [Column("yammer_msg_id")]
        public long YammerID { get; set; }

        [Column("reply_to_yammer_msg_id")]
        public long? ReplyToYammerMessageID { get; set; }

        [Column("likes_count")]
        public int LikesCount { get; set; }

        [Column("followers_count")]
        public int FollowersCount { get; set; }


        public YammerMessage Parent { get; set; }

        [ForeignKey(nameof(Parent))]
        [Column("parent_msg_id")]
        public int? ParentID { get; set; }
    }

    public class YammerMessageImportList : List<YammerMessage>
    {
        public async Task PopulateReplies(AnalyticsEntitiesContext database)
        {
            var messagesThatAreReplies = this.Where(m => m.ReplyToYammerMessageID.HasValue);
            foreach (var messageThatIsReply in messagesThatAreReplies)
            {
                // Check this collection 
                var parentReplies = this.Where(m => m.YammerID == messageThatIsReply.ReplyToYammerMessageID);

                // If not found in this collection, go to DB
                if (parentReplies.Count() == 0)
                {
                    messageThatIsReply.Parent = await database.YammerMessages.Where(m => m.YammerID == messageThatIsReply.ReplyToYammerMessageID).SingleOrDefaultAsync();
                }
                else
                {
                    messageThatIsReply.Parent = this.Where(m => m.YammerID == messageThatIsReply.ReplyToYammerMessageID).First();
                }
            }
        }
    }

    [Table("yammer_msg_to_stream")]
    public class YammerStreamLink : AbstractEFEntity
    {
        public YammerMessage Message { get; set; }

        [ForeignKey(nameof(Message))]
        [Column("message_id")]
        public int MessageID { get; set; }

        public StreamVideo Video { get; set; }


        [ForeignKey(nameof(Video))]
        [Column("stream_id")]
        public int VideoID { get; set; }
    }
}

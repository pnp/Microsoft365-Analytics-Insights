using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace Common.Entities.Entities
{
    [Table("stream_videos")]
    public class StreamVideo : AbstractEFEntityWithName
    {
        [Column("stream_id")]
        public Guid StreamID { get; set; }

        public static Guid GetIdFromUrl(string streamUrlInMsg)
        {
            if (string.IsNullOrEmpty(streamUrlInMsg))
            {
                throw new ArgumentException($"'{nameof(streamUrlInMsg)}' cannot be null or empty", nameof(streamUrlInMsg));
            }

            var guidParser = new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // Do we have a guid & prefix for streams domain?
            if (guidParser.IsMatch(streamUrlInMsg))
            {
                return new Guid(guidParser.Match(streamUrlInMsg).Value);
            }

            return Guid.Empty;
        }
    }
}


using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Engine.Entities
{
    /// <summary>
    /// Data class used to define a time period
    /// TODO: There may be a system type that could be used instead
    /// </summary>
    public class TimePeriod
    {
        public DateTime Start { get; private set; }
        public DateTime End { get; private set; }

        public TimePeriod(DateTime start, DateTime end)
        {
            this.Start = start;
            this.End = end;
        }

        public bool InRange(DateTime dt)
        {
            return this.Start <= dt && this.End >= dt;
        }

        public override string ToString()
        {
            return string.Format("{0} -> {1}", this.Start, this.End);
        }


        /// <summary>
        /// Enumerates the period of time were retrieving metadata for bearing in mind the configuration
        /// and the maximum chunk size and earliest date supported by the API
        /// </summary>
        public static List<TimePeriod> GetScanningTimeChunksFrom(DateTime from, DateTime to)
        {
            return GetScanningTimeChunksFrom(from, to, 0);
        }

        /// <summary>
        /// Enumerates the period of time were retrieving metadata for bearing in mind the configuration
        /// and the maximum chunk size and earliest date supported by the API, with optional overlap
        /// </summary>
        /// <param name="from">Start time</param>
        /// <param name="to">End time</param>
        /// <param name="overlapMinutes">Number of minutes to overlap between chunks to prevent missing events at boundaries</param>
        public static List<TimePeriod> GetScanningTimeChunksFrom(DateTime from, DateTime to, int overlapMinutes)
        {
            if (from > to)
            {
                throw new ArgumentOutOfRangeException();
            }

            // Validate overlap is reasonable (must be less than chunk size to avoid infinite loops)
            const int MAX_OVERLAP_MINUTES = 24 * 60 - 1; // Just under 24 hours
            if (overlapMinutes >= MAX_OVERLAP_MINUTES)
            {
                throw new ArgumentOutOfRangeException(nameof(overlapMinutes), 
                    $"Overlap cannot be >= {MAX_OVERLAP_MINUTES} minutes (chunk size is 24 hours)");
            }

            // We can only extract up to 1 day at a time
            var chunkSize = new TimeSpan(24, 0, 0);

            // Enumerate the chunks of time
            var start = from;
            DateTime end;

            var timeChunks = new List<TimePeriod>();

            while (start < to)
            {
                // Set the end of the chunk
                end = start.Add(chunkSize);
                if (end > to)
                {
                    end = to;
                }

                // Return the value
                timeChunks.Add(new TimePeriod(start, end));

                // Move forwards, accounting for overlap if specified
                // Treat negative overlap as zero (no overlap)
                if (overlapMinutes > 0)
                {
                    // If this was the last chunk (end == to), don't continue looping
                    if (end >= to)
                    {
                        break;
                    }
                    start = end.AddMinutes(-overlapMinutes);
                }
                else
                {
                    start = end;
                }
            }

            // Hack: remove most recent time-chunk as it's likely too small a window, and will generate an error in Activity API
            if (timeChunks.Count > 0)
            {
                timeChunks.RemoveAt(timeChunks.Count - 1);
            }

            return timeChunks;
        }
    }
}

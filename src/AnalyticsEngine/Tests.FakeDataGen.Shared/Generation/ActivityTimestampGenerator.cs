using System;

namespace Tests.FakeDataGen.Generation
{
    internal static class ActivityTimestampGenerator
    {
        /// <summary>
        /// Produces a timestamp inside the requested window with most activity on weekdays
        /// during normal business hours and a small amount of weekend / out-of-hours usage.
        /// </summary>
        public static DateTime Next(Random random, int daysBack)
        {
            return Next(random, daysBack, DateTime.UtcNow);
        }

        /// <summary>
        /// Produces a timestamp inside a window ending at <paramref name="windowEndUtc"/>.
        /// Supplying the endpoint lets multiple generators share the exact same date range.
        /// </summary>
        public static DateTime Next(Random random, int daysBack, DateTime windowEndUtc)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (daysBack < 1) throw new ArgumentOutOfRangeException(nameof(daysBack));

            DateTime now = windowEndUtc.Kind == DateTimeKind.Local
                ? windowEndUtc.ToUniversalTime()
                : windowEndUtc;
            DateTime latestDay = now.Date;
            DateTime earliestDay = latestDay.AddDays(-(daysBack - 1));
            DateTime day = latestDay.AddDays(-random.Next(daysBack));
            if ((day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                && random.Next(100) < 90)
            {
                DateTime precedingWeekday = day;
                while (precedingWeekday.DayOfWeek == DayOfWeek.Saturday
                    || precedingWeekday.DayOfWeek == DayOfWeek.Sunday)
                {
                    precedingWeekday = precedingWeekday.AddDays(-1);
                }

                if (precedingWeekday >= earliestDay)
                {
                    day = precedingWeekday;
                }
                else
                {
                    DateTime followingWeekday = day;
                    while (followingWeekday.DayOfWeek == DayOfWeek.Saturday
                        || followingWeekday.DayOfWeek == DayOfWeek.Sunday)
                    {
                        followingWeekday = followingWeekday.AddDays(1);
                    }

                    if (followingWeekday <= latestDay)
                    {
                        day = followingWeekday;
                    }
                }
            }

            int hour = random.Next(100) < 85
                ? 9 + random.Next(9)
                : random.Next(24);

            DateTime timestamp = day
                .AddHours(hour)
                .AddMinutes(random.Next(60))
                .AddSeconds(random.Next(60));

            if (timestamp > now)
            {
                int secondsAvailable = Math.Max(1, (int)(now - day).TotalSeconds);
                timestamp = day.AddSeconds(random.Next(secondsAvailable));
            }

            return timestamp;
        }
    }
}

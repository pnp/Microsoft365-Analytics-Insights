using System;
using System.Collections.Generic;
using System.Text;

namespace DataUtils
{
    public static class DateTimeUtils
    {

        /// <summary>
        /// Get each date in date-range. Only returns date part. 
        /// </summary>
        public static IEnumerable<DateTime> EachDay(this DateTime from, DateTime thru)
        {
            if (from <= thru)
            {
                for (var day = from.Date; day.Date <= thru.Date; day = day.AddDays(1))
                    yield return day;
            }
            if (from > thru)
            {
                for (var day = from.Date; day.Date >= thru.Date; day = day.AddDays(-1))
                    yield return day;
            }

        }
    }
}

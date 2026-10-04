using System;
using System.Collections.Generic;
using System.Globalization;

namespace Common.Entities.CopilotAuditBackfill
{
    public static class CopilotAuditBackfillSlicer
    {
        public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(180);

        public static DateTime ClampStart(DateTime? requestedStartUtc, DateTime nowUtc)
        {
            nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            var earliest = nowUtc.Subtract(RetentionWindow);
            if (!requestedStartUtc.HasValue) return earliest;
            var requested = DateTime.SpecifyKind(requestedStartUtc.Value.ToUniversalTime(), DateTimeKind.Utc);
            if (requested < earliest) return earliest;
            if (requested > nowUtc) return nowUtc;
            return requested;
        }

        public static List<CopilotAuditBackfillSlice> BuildDaySlices(DateTime startUtc, DateTime endUtc)
        {
            var slices = new List<CopilotAuditBackfillSlice>();
            var cursor = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc);
            var floor = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
            while (cursor > floor)
            {
                var sliceStart = cursor.TimeOfDay == TimeSpan.Zero ? cursor.AddDays(-1) : cursor.Date;
                if (sliceStart < floor) sliceStart = floor;
                slices.Add(new CopilotAuditBackfillSlice { StartUtc = sliceStart, EndUtc = cursor });
                cursor = sliceStart;
            }
            return slices;
        }


        public static List<CopilotAuditBackfillSlice> SplitForTruncation(CopilotAuditBackfillSlice slice)
        {
            return slice.SplitLevel == 0
                ? SplitByHours(slice, 6)
                : SplitIntoHours(slice);
        }

        private static List<CopilotAuditBackfillSlice> SplitByHours(CopilotAuditBackfillSlice slice, int hours)
        {
            var result = new List<CopilotAuditBackfillSlice>();
            var cursor = slice.EndUtc;
            while (cursor > slice.StartUtc)
            {
                var start = cursor.AddHours(-hours);
                if (start < slice.StartUtc) start = slice.StartUtc;
                result.Add(new CopilotAuditBackfillSlice { StartUtc = start, EndUtc = cursor, SplitLevel = slice.SplitLevel + 1 });
                cursor = start;
            }
            return result;
        }

        public static List<CopilotAuditBackfillSlice> SplitIntoHours(CopilotAuditBackfillSlice slice)
        {
            var result = new List<CopilotAuditBackfillSlice>();
            var cursor = slice.EndUtc;
            while (cursor > slice.StartUtc)
            {
                var start = cursor.AddHours(-1);
                if (start < slice.StartUtc) start = slice.StartUtc;
                result.Add(new CopilotAuditBackfillSlice { StartUtc = start, EndUtc = cursor, SplitLevel = slice.SplitLevel + 1 });
                cursor = start;
            }
            return result;
        }

        public static string DayKey(DateTime valueUtc) => valueUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

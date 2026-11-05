using System;
using System.Collections.Generic;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    internal static class DemoRandom
    {
        public static uint Value(int seed, int user, int day, int salt)
        {
            unchecked
            {
                uint x = (uint)seed ^ ((uint)user * 0x9e3779b9u) ^ ((uint)day * 0x85ebca6bu) ^ ((uint)salt * 0xc2b2ae35u);
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                return x ^ (x >> 16);
            }
        }

        public static Guid Id(int seed, short family, int user, int day = 0, int slot = 0)
        {
            var bytes = new byte[8];
            Array.Copy(BitConverter.GetBytes(user), bytes, 4);
            Array.Copy(BitConverter.GetBytes((ushort)day), 0, bytes, 4, 2);
            Array.Copy(BitConverter.GetBytes((ushort)slot), 0, bytes, 6, 2);
            return new Guid(seed, family, 1, bytes);
        }
    }

    internal sealed class DemoCalendar
    {
        private readonly DateTime _start;
        private readonly int _days;
        private readonly Dictionary<string, OfficeDay[]> _windows = new Dictionary<string, OfficeDay[]>(StringComparer.Ordinal);

        public DemoCalendar(DemoOptions options) { _start = options.Start; _days = options.Days; }

        public static bool IsWeekday(DateTime day) =>
            day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday;

        public static bool IsWorkingDate(DateTime day) =>
            IsWeekday(day) && !(day.Month == 1 && day.Day == 1) && !(day.Month == 12 && day.Day == 25);

        public static string ZoneFor(SeedDataCatalogue.UserProfile profile)
        {
            switch (profile.UsageLocation)
            {
                case "US":
                    return profile.StateOrProvince == "New York" || profile.StateOrProvince == "Massachusetts" ? "Eastern Standard Time"
                        : profile.StateOrProvince == "Texas" ? "Central Standard Time" : "Pacific Standard Time";
                case "CA": return profile.StateOrProvince == "British Columbia" ? "Pacific Standard Time" : "Eastern Standard Time";
                case "GB": case "IE": return "GMT Standard Time";
                case "FR": case "ES": return "Romance Standard Time";
                case "PL": return "Central European Standard Time";
                case "GR": return "GTB Standard Time";
                case "BR": return "E. South America Standard Time";
                case "MX": return "Central Standard Time (Mexico)";
                case "IN": return "India Standard Time";
                case "JP": return "Tokyo Standard Time";
                case "AU": return "AUS Eastern Standard Time";
                case "SG": return "Singapore Standard Time";
                case "AE": return "Arabian Standard Time";
                case "ZA": return "South Africa Standard Time";
                default: return "W. Europe Standard Time";
            }
        }

        public DateTime Timestamp(string zoneId, int day, uint jitter, int slot = 0)
        {
            if (!_windows.TryGetValue(zoneId, out var windows))
            {
                windows = new OfficeDay[_days];
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                for (int i = 0; i < windows.Length; i++)
                {
                    var date = _start.AddDays(i);
                    var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    var first = TimeZoneInfo.ConvertTimeToUtc(local.AddHours(9), zone);
                    var last = TimeZoneInfo.ConvertTimeToUtc(local.AddHours(17), zone);
                    // Keep local AND UTC dates on the same weekday; avoids a Sydney Monday
                    // morning looking like an invented Sunday in UTC daily reports.
                    if (first < date) first = date;
                    if (last > date.AddDays(1)) last = date.AddDays(1);
                    windows[i] = new OfficeDay { Start = first, Minutes = (int)(last - first).TotalMinutes };
                }
                _windows.Add(zoneId, windows);
            }
            var window = windows[day];
            return window.Start.AddMinutes((jitter + (uint)(slot * 37)) % (uint)(window.Minutes - 1));
        }

        private struct OfficeDay { public DateTime Start; public int Minutes; }
    }
}

using Common.Entities.LicenceActivity;
using System;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Reporting window requested by the Copilot Adoption page.
    /// </summary>
    public sealed class CopilotAdoptionDateRange
    {
        public static readonly int[] RollingWindowDays = { 7, 28, 90, 180 };

        public DateTime FromUtc { get; private set; }
        public DateTime ToInclusiveUtc { get; private set; }
        public DateTime ToExclusiveUtc { get; private set; }
        public int WindowDays { get; private set; }
        public bool UsesExplicitDates { get; private set; }

        private CopilotAdoptionDateRange() { }

        public static CopilotAdoptionDateRange Create(int windowDays, string from, string to, DateTime nowUtc)
        {
            if ((from == null) != (to == null))
                throw new ArgumentException("copilotAdoption.error.missingDate");

            if (from == null)
            {
                var normalised = NormaliseWindowDays(windowDays);
                return new CopilotAdoptionDateRange
                {
                    FromUtc = CopilotAdoptionScoring.WindowStartUtc(nowUtc, normalised),
                    ToInclusiveUtc = nowUtc,
                    ToExclusiveUtc = nowUtc,
                    WindowDays = normalised,
                    UsesExplicitDates = false,
                };
            }

            var start = ParseDate(from);
            var end = ParseDate(to);
            var days = (end - start).Days + 1;
            if (days < LicenceActivityQuery.MinimumDays
                || days > LicenceActivityQuery.MaximumDays
                || end >= nowUtc.Date)
            {
                throw new ArgumentException("copilotAdoption.error.invalidDateRange");
            }

            if (start.Year < 1753)
                throw new ArgumentException("copilotAdoption.error.dateTooEarly");

            return new CopilotAdoptionDateRange
            {
                FromUtc = start,
                ToInclusiveUtc = end,
                ToExclusiveUtc = end.AddDays(1),
                WindowDays = days,
                UsesExplicitDates = true,
            };
        }

        public static int NormaliseWindowDays(int windowDays)
        {
            if (RollingWindowDays.Contains(windowDays)) return windowDays;
            return RollingWindowDays
                .OrderBy(allowed => Math.Abs(allowed - windowDays))
                .ThenBy(allowed => allowed)
                .First();
        }

        private static DateTime ParseDate(string value)
        {
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
                throw new ArgumentException("copilotAdoption.error.invalidDateFormat");
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        }
    }
}

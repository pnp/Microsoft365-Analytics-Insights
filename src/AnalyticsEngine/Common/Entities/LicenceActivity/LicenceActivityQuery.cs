using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Globalization;
using System.Linq;

namespace Common.Entities.LicenceActivity
{
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityQuery
    {
        public const int MinimumDays = 7;
        public const int MaximumDays = 180;
        public const int MaximumRows = 100;
        public const int MaximumPage = 10000;
        public static readonly string[] Workloads = { "teams", "outlook", "onedrive", "sharepoint", "copilot" };
        private static readonly string[] Sorts = { "upn", "activity", "lastActivity" };

        public string From { get; private set; }
        public string To { get; private set; }
        public int? DepartmentId { get; private set; }
        public int? CountryId { get; private set; }
        public int? LicenceTypeId { get; private set; }
        public string Workload { get; private set; }
        public string Search { get; private set; }
        public string Sort { get; private set; }
        public string Direction { get; private set; }
        public int Top { get; private set; }
        public int Page { get; private set; }
        public int PageSize { get; private set; }

        [JsonIgnore]
        public DateTime FromUtc => ParseDate(From);
        [JsonIgnore]
        public DateTime ToUtc => ParseDate(To);
        [JsonIgnore]
        public DateTime EndExclusiveUtc => ToUtc.AddDays(1);
        [JsonIgnore]
        public int Days => (ToUtc - FromUtc).Days + 1;

        private LicenceActivityQuery() { }

        public static LicenceActivityQuery Create(
            string from, string to, DateTime nowUtc, int? departmentId = null, int? countryId = null,
            int? licenceTypeId = null, string workload = "teams", string search = null,
            string sort = "upn", string direction = "asc", int top = 10, int page = 1, int pageSize = 50)
        {
            if ((from == null) != (to == null))
                throw new ArgumentException("Supply both from and to dates in YYYY-MM-DD format.");
            var settled = nowUtc.Date.AddDays(-3);
            var end = to == null ? settled.AddDays(-(int)settled.DayOfWeek) : ParseDate(to);
            var start = from == null ? end.AddDays(-27) : ParseDate(from);
            var days = (end - start).Days + 1;
            if (days < MinimumDays || days > MaximumDays || end >= nowUtc.Date)
                throw new ArgumentException("Choose 7 to 180 inclusive UTC dates, ending before today. Custom ranges are never rounded.");
            if (start.Year < 1753)
                throw new ArgumentException("The earliest supported date is 1753-01-01.");
            if (departmentId < 0 || countryId < 0 || licenceTypeId <= 0)
                throw new ArgumentException("Licence IDs must be positive; demographic IDs must be zero (unknown) or positive.");
            if (!Workloads.Contains(workload, StringComparer.Ordinal))
                throw new ArgumentException("Choose teams, outlook, onedrive, sharepoint or copilot.");
            if (!Sorts.Contains(sort, StringComparer.Ordinal) || (direction != "asc" && direction != "desc"))
                throw new ArgumentException("Choose a supported sort and asc or desc direction.");
            if (top < 1 || top > MaximumRows || pageSize < 1 || pageSize > MaximumRows || page < 1 || page > MaximumPage)
                throw new ArgumentException("Top and pageSize must be 1 to 100; page must be 1 to 10000.");
            search = (search ?? string.Empty).Trim();
            if (search.Length > 100 || search.Any(char.IsControl))
                throw new ArgumentException("Search must contain at most 100 characters and no control characters.");

            return new LicenceActivityQuery
            {
                From = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                To = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DepartmentId = departmentId, CountryId = countryId, LicenceTypeId = licenceTypeId,
                Workload = workload, Search = search, Sort = sort, Direction = direction,
                Top = top, Page = page, PageSize = pageSize
            };
        }

        public LicenceActivityQuery ForUsers(
            int licenceTypeId, string workload, string search, string sort, string direction,
            int top, int page, int pageSize, DateTime nowUtc) =>
            Create(From, To, nowUtc, DepartmentId, CountryId, licenceTypeId, workload, search, sort, direction, top, page, pageSize);

        public string CacheKey() => JsonConvert.SerializeObject(this);

        private static DateTime ParseDate(string value)
        {
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
                throw new ArgumentException("Dates must use YYYY-MM-DD format.");
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        }
    }
}

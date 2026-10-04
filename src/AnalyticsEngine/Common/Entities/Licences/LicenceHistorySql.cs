using System;

using System.Text;



namespace Common.Entities.Licences

{

    /// <summary>SQL read model for licence assignment history.</summary>

    public static class LicenceHistorySql

    {

        /// <summary>

        /// Users who held any of the supplied licence types at any point during the UTC day beginning

        /// at <c>@dayUtc</c>. Licence ids are supplied as <c>@license0</c> ... <c>@licenseN</c>.

        /// </summary>

        public static string BuildHoldersOnDay(int licenseTypeCount)

        {

            if (licenseTypeCount < 1) throw new ArgumentOutOfRangeException(nameof(licenseTypeCount));

            return @"DECLARE @dayStart datetime2(0) = @dayUtc;

DECLARE @dayEnd datetime2(0) = DATEADD(day, 1, @dayStart);



SELECT DISTINCT h.user_id

FROM dbo.user_license_history AS h

INNER JOIN (VALUES " + LicenseValues(licenseTypeCount) + @") AS wanted(license_type_id)

    ON wanted.license_type_id = h.license_type_id

WHERE h.valid_from_utc < @dayEnd

  AND (h.valid_to_utc IS NULL OR h.valid_to_utc > @dayStart);";

        }



        /// <summary>

        /// Holdings for the supplied licence types that overlap <c>[@fromUtc, @toUtc)</c>, with the

        /// overlapped duration in days. Licence ids are supplied as <c>@license0</c> ... <c>@licenseN</c>.

        /// </summary>

        public static string BuildHoldersInRange(int licenseTypeCount)

        {

            if (licenseTypeCount < 1) throw new ArgumentOutOfRangeException(nameof(licenseTypeCount));

            return @"SELECT

    h.user_id,

    h.license_type_id,

    days_held = SUM(CAST(DATEDIFF(second,

        CASE WHEN h.valid_from_utc < @fromUtc THEN @fromUtc ELSE h.valid_from_utc END,

        CASE WHEN h.valid_to_utc IS NULL OR h.valid_to_utc > @toUtc THEN @toUtc ELSE h.valid_to_utc END) AS decimal(18, 4)) / 86400.0)

FROM dbo.user_license_history AS h

INNER JOIN (VALUES " + LicenseValues(licenseTypeCount) + @") AS wanted(license_type_id)

    ON wanted.license_type_id = h.license_type_id

WHERE h.valid_from_utc < @toUtc

  AND (h.valid_to_utc IS NULL OR h.valid_to_utc > @fromUtc)

GROUP BY h.user_id, h.license_type_id;";

        }



        private static string LicenseValues(int count)

        {

            var sql = new StringBuilder(count * 12);

            for (var i = 0; i < count; i++)

            {

                if (i > 0) sql.Append(',');

                sql.Append("(@license").Append(i).Append(')');

            }

            return sql.ToString();

        }

    }

}

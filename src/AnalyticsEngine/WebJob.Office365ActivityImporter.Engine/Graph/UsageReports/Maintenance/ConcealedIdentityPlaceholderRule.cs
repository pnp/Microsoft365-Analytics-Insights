using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Maintenance
{
    /// <summary>
    /// What a placeholder user that an older build created from a concealed usage-report identity looks like in
    /// <c>dbo.users</c>: a <c>user_name</c> of exactly 32 lower-case hexadecimal characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a tenant has "Display concealed user, group, and site names in all reports" turned on (Microsoft's default
    /// since 2021), Graph replaces each user principal name with a 32-character upper-case hexadecimal hash and no
    /// <c>@</c> - Microsoft's own example response for <c>getMicrosoft365CopilotUsageUserDetail</c> has
    /// <c>"userPrincipalName": "DC8C64D6EC3A3AA17481D7E5EB5B68A6"</c>. Before <see cref="UsageReportIdentityPolicy"/>
    /// made the loaders refuse such rows, every one went through
    /// <see cref="AbstractUserActivityUserDetail.GetOrCreateLookup"/>, which looks the user up - and creates it - by
    /// <see cref="AbstractUserActivityUserDetail.LookupKey"/>, the value lower-cased. So the stored form of every
    /// placeholder is the hash in lower case, and that exact form is the rule.
    /// </para>
    /// <para>
    /// Deliberately narrower than <see cref="UsageReportIdentityPolicy.IsConcealedUserIdentity"/>, which refuses
    /// anything that is not a routable UPN: a cleanup deletes, so it only takes what it can prove is a hash. A different
    /// length, an upper-case letter (the importer never stored one), an <c>@</c>, a space or any other character means
    /// the row is kept.
    /// </para>
    /// </remarks>
    public static class ConcealedIdentityPlaceholderRule
    {
        /// <summary>Characters in the hash Graph substitutes for a user (an MD5-length hexadecimal string).</summary>
        public const int HashLength = 32;

        /// <summary>True only for exactly <see cref="HashLength"/> characters, each <c>0-9</c> or <c>a-f</c>.</summary>
        public static bool IsPlaceholderUserName(string userName)
        {
            if (userName == null || userName.Length != HashLength) return false;

            foreach (var c in userName)
            {
                var isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isLowerHex) return false;
            }
            return true;
        }

        /// <summary>
        /// <see cref="IsPlaceholderUserName"/> in T-SQL, over <c>dbo.users</c> aliased <c>u</c>. <c>DATALENGTH</c>, not
        /// <c>LEN</c>: both <c>LEN</c> and <c>LIKE</c> ignore trailing spaces, and <c>user_name</c> is <c>varchar</c>, one byte
        /// per character. A binary collation, because the column's own is case-insensitive and would let <c>[a-f]</c> match
        /// upper case too. The <c>@</c> test is implied by the character class; it is spelled out for the reader.
        /// </summary>
        internal const string SqlUserNameMatches =
            "DATALENGTH(u.user_name) = 32 AND u.user_name COLLATE Latin1_General_BIN2 NOT LIKE '%[^0-9a-f]%' AND CHARINDEX('@', u.user_name) = 0";

        /// <summary>
        /// The row was never touched by the user import, which fills <c>azure_ad_id</c> and <c>mail</c> from Entra ID: a
        /// placeholder was created with both empty. <c>DATALENGTH</c>, so a value of spaces does not pass for empty.
        /// </summary>
        internal const string SqlNeverSeenByUserImport =
            "(u.azure_ad_id IS NULL OR DATALENGTH(u.azure_ad_id) = 0) AND (u.mail IS NULL OR DATALENGTH(u.mail) = 0)";
    }
}

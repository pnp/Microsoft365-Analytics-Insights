using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>Where a filter dimension's values come from.</summary>
    public enum UserFilterDimensionKind
    {
        /// <summary>A standard Entra ID attribute the user-metadata import already stores on <c>dbo.users</c>.</summary>
        Entra = 1,

        /// <summary>An admin-defined user organisation type (see <c>Common.Entities.UserOrgs</c>).</summary>
        Custom = 2,
    }

    /// <summary>
    /// The attributes a user filter can test, keyed by stable identifiers.
    /// </summary>
    /// <remarks>
    /// <para>The keys are an API contract with the portal, which maps each Entra key to a translated
    /// label and falls back to the key itself for one it does not recognise - so a key must never be
    /// renamed. Custom organisation types are keyed <c>org:{id}</c> and labelled with the name the admin
    /// gave them, which is tenant data and never translated.</para>
    /// <para>Everything here is derived from columns the user-metadata import already maintains, so
    /// the filter costs no schema change and no extra Graph call.</para>
    /// </remarks>
    public static class UserFilterDimensions
    {
        /// <summary>
        /// The person's sign-in name (<c>userPrincipalName</c>). Matched as free text as well as by
        /// value, so "User name contains smith" - or "does not contain svc-", to leave service accounts
        /// out - works without anyone having to pick 200,000 names from a list.
        /// </summary>
        public const string UserName = "userName";

        public const string Department = "department";
        public const string JobTitle = "jobTitle";
        public const string CompanyName = "companyName";
        public const string OfficeLocation = "officeLocation";
        public const string Country = "country";
        public const string StateOrProvince = "stateOrProvince";
        public const string UsageLocation = "usageLocation";

        /// <summary>
        /// The organisation a person's sign-in name belongs to, derived exactly as the adoption report's
        /// email-domain breakdown derives it (see <c>CopilotAdoptionEmailDomain</c>).
        /// </summary>
        public const string EmailDomain = "emailDomain";

        /// <summary>Member or guest, from the <c>#EXT#</c> marker Entra writes into a guest's UPN.</summary>
        public const string UserType = "userType";

        /// <summary>Whether the account is enabled in Entra ID.</summary>
        public const string AccountStatus = "accountStatus";

        /// <summary>The person's direct manager, by UPN.</summary>
        public const string Manager = "manager";

        /// <summary>
        /// Everyone who reports to a manager at any level - the whole organisation beneath them, not
        /// including the manager.
        /// </summary>
        public const string ManagementChain = "managementChain";

        /// <summary>Prefix of a custom organisation type's key; the rest is the org type id.</summary>
        public const string CustomPrefix = "org:";

        /// <summary>
        /// The Entra dimensions, in the order the portal offers them: who someone is - their name and
        /// the organisation their address belongs to - first, then where they sit in the business, then
        /// their account and their place in the hierarchy.
        /// </summary>
        public static readonly IReadOnlyList<string> EntraKeys = new[]
        {
            UserName,
            EmailDomain,
            Department,
            JobTitle,
            CompanyName,
            OfficeLocation,
            Country,
            StateOrProvince,
            UsageLocation,
            UserType,
            AccountStatus,
            Manager,
            ManagementChain,
        };

        private static readonly HashSet<string> EntraKeySet = new HashSet<string>(EntraKeys, StringComparer.Ordinal);

        public static string ForOrgType(int orgTypeId)
        {
            return CustomPrefix + orgTypeId.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsEntra(string key)
        {
            return key != null && EntraKeySet.Contains(key);
        }

        /// <summary>Reads the org type id out of a custom key. Only positive ids are accepted.</summary>
        public static bool TryParseOrgTypeId(string key, out int orgTypeId)
        {
            orgTypeId = 0;
            if (key == null || !key.StartsWith(CustomPrefix, StringComparison.Ordinal)) return false;

            var digits = key.Substring(CustomPrefix.Length);

            // Digits only: int.Parse would also accept "+12", " 12" and "012", which would give one org
            // type several keys - and the echoed filter would then disagree with the one the page sent.
            if (digits.Length == 0 || digits.Length > 10 || digits.Any(c => c < '0' || c > '9') || digits[0] == '0')
            {
                return false;
            }

            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out orgTypeId) && orgTypeId > 0;
        }

        /// <summary>True for a key this build understands the shape of, whether or not it exists right now.</summary>
        public static bool IsWellFormed(string key)
        {
            return IsEntra(key) || TryParseOrgTypeId(key, out _);
        }

        public static UserFilterDimensionKind KindOf(string key)
        {
            return IsEntra(key) ? UserFilterDimensionKind.Entra : UserFilterDimensionKind.Custom;
        }

        /// <summary>
        /// Whether "contains" makes sense on this dimension.
        /// </summary>
        /// <remarks>
        /// Not on the fixed-value dimensions, whose values are product tokens rather than text, and not
        /// on the management chain, which matches a hierarchy rather than a string.
        /// </remarks>
        public static bool SupportsTextMatch(string key)
        {
            return !HasFixedValues(key) && !string.Equals(key, ManagementChain, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether the dimension's values are product tokens from <see cref="UserFilterTokens"/> rather than
        /// tenant data - which the portal translates, where it must never translate tenant data.
        /// </summary>
        public static bool HasFixedValues(string key)
        {
            return string.Equals(key, UserType, StringComparison.Ordinal)
                || string.Equals(key, AccountStatus, StringComparison.Ordinal);
        }

        /// <summary>The tokens a fixed-value dimension accepts, or <c>null</c> for any other dimension.</summary>
        public static IReadOnlyList<string> FixedValues(string key)
        {
            if (string.Equals(key, UserType, StringComparison.Ordinal)) return UserFilterTokens.UserTypes;
            if (string.Equals(key, AccountStatus, StringComparison.Ordinal)) return UserFilterTokens.AccountStatuses;
            return null;
        }

        /// <summary>
        /// The English name of an Entra dimension, for the server's own English artefacts - the Excel
        /// workbook and CSV exports. The portal never shows these: it has its own translated labels.
        /// </summary>
        public static string EnglishName(string key)
        {
            switch (key)
            {
                case UserName: return "User name";
                case Department: return "Department";
                case JobTitle: return "Job title";
                case CompanyName: return "Company";
                case OfficeLocation: return "Office location";
                case Country: return "Country or region";
                case StateOrProvince: return "State or province";
                case UsageLocation: return "Usage location";
                case EmailDomain: return "Email domain";
                case UserType: return "User type";
                case AccountStatus: return "Account status";
                case Manager: return "Manager";
                case ManagementChain: return "Management chain";
                default: return key;
            }
        }
    }

    /// <summary>
    /// The values of the fixed-value dimensions. Stable tokens, translated by the portal.
    /// </summary>
    public static class UserFilterTokens
    {
        public const string Member = "member";
        public const string Guest = "guest";
        public const string Enabled = "enabled";
        public const string Disabled = "disabled";

        public static readonly IReadOnlyList<string> UserTypes = new[] { Member, Guest };

        public static readonly IReadOnlyList<string> AccountStatuses = new[] { Enabled, Disabled };

        /// <summary>English wording for a token, for the server's own English artefacts.</summary>
        public static string EnglishName(string token)
        {
            switch (token)
            {
                case Member: return "Member";
                case Guest: return "Guest";
                case Enabled: return "Enabled";
                case Disabled: return "Disabled";
                default: return token;
            }
        }
    }
}

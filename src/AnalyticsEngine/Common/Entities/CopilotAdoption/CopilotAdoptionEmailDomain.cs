using System;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Turns a user principal name into the email domain Copilot adoption is compared across.
    ///
    /// <para>The domain is the closest thing this schema has to "which organisation is this person
    /// from". A single Microsoft 365 tenant routinely carries several verified domains - acquisitions
    /// that were never rebranded, subsidiaries, a separate contractor domain - and those populations
    /// adopt Copilot at very different rates. Department answers "which function"; domain answers
    /// "which company", and on a tenant built by acquisition that is the more actionable split,
    /// because enablement, training budget and licence ownership usually follow the company rather
    /// than the org chart.</para>
    ///
    /// <para>Derived in memory from the UPN that every user query already selects, so this dimension
    /// costs no extra join, no extra column and no migration.</para>
    /// </summary>
    public static class CopilotAdoptionEmailDomain
    {
        /// <summary>
        /// Shown in place of a domain that could not be derived - a UPN that is missing, or that is not
        /// in <c>name@domain</c> form at all.
        /// </summary>
        /// <remarks>
        /// Deliberately not "(unknown)": the rows are real seats and their absence from every domain
        /// bucket has to be visible, or the domain columns quietly stop adding up to the headline.
        /// </remarks>
        public const string NoDomainLabel = "(no domain)";

        /// <summary>
        /// The marker Entra puts in a guest's UPN. See <see cref="CopilotAdoptionSql.GuestUserNamePattern"/>:
        /// there is no <c>userType</c> column in this schema, so this is the only signal available.
        /// </summary>
        private const string ExternalMarker = "#EXT#";

        /// <summary>
        /// Microsoft's own tenant suffix. Every tenant has one and it is never a customer's
        /// organisation, so it is the one domain that answers "which tenant" rather than "which
        /// company" - the same distinction that makes a guest's inviting tenant the wrong answer.
        /// </summary>
        private const string DefaultTenantSuffix = ".onmicrosoft.com";

        /// <summary>
        /// The email domain for a user, lower-cased, or <c>null</c> when one cannot be derived.
        /// </summary>
        /// <param name="userPrincipalName">
        /// The Entra UPN, as stored in <c>dbo.users.user_name</c>.
        /// </param>
        /// <param name="mail">
        /// The mail address, used only when the UPN yields nothing. Kept as a fallback rather than as
        /// the primary source because <c>mail</c> is frequently null in this schema while the UPN never
        /// is, and because two people in the same company can have a vanity mail domain that differs
        /// from the sign-in domain - grouping on that would split one organisation in two.
        /// </param>
        /// <remarks>
        /// <para><b>Guests are attributed to their home organisation, not to the host tenant.</b> Entra
        /// writes an invited guest as <c>alice_contoso.com#EXT#@fabrikam.onmicrosoft.com</c>, so the
        /// part after the final <c>@</c> is the <i>inviting</i> tenant and is identical for every guest
        /// in the directory. Grouping on it would collapse every partner, supplier and contractor into
        /// one meaningless bucket named after the host. The home domain encoded before <c>#EXT#</c> is
        /// the one an admin actually wants to compare, and it is the reason this method is not a
        /// one-line <c>Split('@').Last()</c>.</para>
        /// <para>Lower-cased because DNS names are case-insensitive, so <c>Contoso.com</c> and
        /// <c>contoso.com</c> are one organisation and must never appear as two rows.
        /// <see cref="string.ToLowerInvariant"/> rather than <c>ToLower()</c> so the result does not
        /// change with the server's culture - under a Turkish locale <c>ToLower()</c> maps <c>I</c> to
        /// a dotless <c>ı</c> and would split a domain containing an upper-case I into two.</para>
        /// <para><b>The tenant's own <c>*.onmicrosoft.com</c> suffix loses to the mail address.</b>
        /// Some tenants pin every UPN to the default suffix and carry the real per-company domain on
        /// <c>mail</c> instead. Taking the UPN there would put every subsidiary in one bucket named
        /// after the tenant and silently defeat the comparison - the same mistake as attributing a
        /// guest to the tenant that invited them. When <c>mail</c> yields nothing better, the default
        /// suffix is still returned rather than dropping the person entirely.</para>
        /// </remarks>
        public static string From(string userPrincipalName, string mail = null)
        {
            var fromUpn = FromSingle(userPrincipalName);

            if (fromUpn == null) return FromSingle(mail);
            if (!IsDefaultTenantDomain(fromUpn)) return fromUpn;

            // The UPN only tells us which tenant this is. Prefer a mail domain that names a company.
            var fromMail = FromSingle(mail);

            return fromMail != null && !IsDefaultTenantDomain(fromMail) ? fromMail : fromUpn;
        }

        /// <summary>
        /// Whether this is a Microsoft-issued tenant suffix rather than a customer's own domain.
        /// </summary>
        public static bool IsDefaultTenantDomain(string domain)
        {
            return domain != null
                && domain.EndsWith(DefaultTenantSuffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether this UPN belongs to an external guest rather than a member of the tenant.
        /// </summary>
        /// <remarks>
        /// Worth showing next to a domain because it changes what the row means: a domain made up of
        /// guests is a partner organisation being collaborated with, not a part of the business that
        /// can be given Copilot training.
        /// </remarks>
        public static bool IsExternalGuest(string userPrincipalName)
        {
            return userPrincipalName != null
                && userPrincipalName.IndexOf(ExternalMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The domain, or <see cref="NoDomainLabel"/> when there is none - the form used as a grouping
        /// key and shown on screen.
        /// </summary>
        public static string Label(string domain)
        {
            return string.IsNullOrWhiteSpace(domain) ? NoDomainLabel : domain;
        }

        /// <summary>
        /// Normalises a caller-supplied domain (a query-string value) into the form
        /// <see cref="From"/> produces, so a filter matches whatever case the caller typed.
        /// Returns <c>null</c> for anything blank.
        /// </summary>
        /// <remarks>
        /// A leading <c>@</c> is tolerated because an admin copying a domain out of an address
        /// naturally brings it along, and <see cref="NoDomainLabel"/> is passed through unchanged so
        /// the "(no domain)" bucket is selectable like any other.
        /// </remarks>
        public static string Normalise(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var trimmed = value.Trim();

            if (string.Equals(trimmed, NoDomainLabel, StringComparison.OrdinalIgnoreCase))
            {
                return NoDomainLabel;
            }

            trimmed = trimmed.TrimStart('@');
            if (trimmed.Length == 0) return null;

            // An admin may paste a whole address rather than a domain. Reading it as one is strictly
            // more useful than failing to match anything.
            var at = trimmed.LastIndexOf('@');
            if (at >= 0)
            {
                return From(trimmed);
            }

            return trimmed.ToLowerInvariant();
        }

        private static string FromSingle(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;

            var value = address.Trim();

            // A guest's home domain is encoded before the #EXT# marker, with the '@' replaced by '_':
            // alice_contoso.com#EXT#@fabrikam.onmicrosoft.com -> contoso.com. Take the LAST underscore,
            // because the local part itself commonly contains one (first_last_contoso.com#EXT#@...).
            var marker = value.IndexOf(ExternalMarker, StringComparison.OrdinalIgnoreCase);
            if (marker > 0)
            {
                var encoded = value.Substring(0, marker);
                var separator = encoded.LastIndexOf('_');

                if (separator >= 0 && separator < encoded.Length - 1)
                {
                    var home = encoded.Substring(separator + 1).Trim();
                    // Only trust it if it looks like a domain. A guest invited by mail alias rather
                    // than by address can produce an encoded part with no dot in it, and reporting
                    // that as a domain would invent an organisation that does not exist.
                    if (home.Length > 0 && home.IndexOf('.') > 0 && !home.EndsWith("."))
                    {
                        return home.ToLowerInvariant();
                    }
                }

                // A guest whose home domain could not be read is still a guest, and attributing it to
                // the inviting tenant would silently merge it into the host organisation. Unknown is
                // the honest answer.
                return null;
            }

            var at = value.LastIndexOf('@');
            if (at < 0 || at == value.Length - 1) return null;

            var domain = value.Substring(at + 1).Trim();

            return domain.Length == 0 ? null : domain.ToLowerInvariant();
        }
    }
}

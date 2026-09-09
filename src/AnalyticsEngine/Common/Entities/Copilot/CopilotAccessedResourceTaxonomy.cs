using System;
using System.Collections.Generic;

namespace Common.Entities.Copilot
{
    /// <summary>
    /// What a Copilot audit record's <c>AccessedResources[].Type</c> value actually describes.
    ///
    /// Microsoft does not publish an enumeration for that field. The Office 365 Management Activity
    /// API schema declares it as an open <c>Edm.String</c>, and the Purview documentation describes
    /// it as: "the type of resource that was accessed. It can contain values like the filetype
    /// extension (pptx, docx, etc.) or describe the type of resource (for non-SharePoint resources)"
    /// (https://learn.microsoft.com/en-us/purview/audit-copilot#common-properties-in-copilot-audit-logs,
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/copilot-schema).
    ///
    /// In practice the one field therefore carries values from several unrelated namespaces at once -
    /// file kinds, Microsoft Graph entity names, how the resource was used, and grounding-source
    /// markers - which is why nothing in this product may treat it as a single content taxonomy.
    /// See issues #468 and #469.
    /// </summary>
    public enum CopilotResourceTypeKind
    {
        /// <summary>
        /// A value this product does not recognise, including a missing one. The default, so a value
        /// Microsoft has not used yet is visibly unclassified rather than silently folded into one of
        /// the buckets below. Unclassified is NOT a synonym for "not tenant content".
        /// </summary>
        Unclassified = 0,

        /// <summary>
        /// The value names a kind of organisational content - a file kind (<c>docx</c>) or a Microsoft
        /// Graph entity (<c>EmailMessage</c>). This is the only kind that answers "what content is
        /// Copilot working on".
        /// </summary>
        TenantContent = 1,

        /// <summary>
        /// The value describes how the resource was used rather than what it is - <c>CITATION</c>
        /// means it was shown to the user as a cited source. Says nothing about whether the resource
        /// was tenant content or a web page, and a cited file is typed this way INSTEAD of by its
        /// file kind, which is why the file-kind counts undercount real file references.
        /// </summary>
        UsageRole = 2,

        /// <summary>
        /// The value marks grounding from outside the tenant, e.g. <c>WebSearchQuery</c>. Not
        /// organisational content.
        /// </summary>
        ExternalGrounding = 3,
    }

    /// <summary>
    /// Interprets the fields of a Copilot audit record's <c>AccessedResources</c> entry.
    ///
    /// Deliberately one shared implementation: the credit estimate (which decides the 10-credit
    /// tenant-graph charge) and the Copilot Adoption "what Copilot referenced" chart used to make the
    /// same category error independently, in code that had no way of staying in step.
    /// </summary>
    public static class CopilotAccessedResourceTaxonomy
    {
        /// <summary>
        /// The label used wherever a resource has no <c>Type</c> at all. Deliberately not "WebPage" or
        /// any other guess: a missing type says nothing about where the resource came from.
        /// </summary>
        public const string UnknownTypeLabel = "(unknown)";

        /// <summary>
        /// Values that name a kind of organisational content. Every entry is either a file kind or a
        /// Microsoft Graph entity name.
        ///
        /// This is a recognition list, not a specification: because Microsoft publishes no
        /// enumeration, anything absent is <see cref="CopilotResourceTypeKind.Unclassified"/> and must
        /// never be read as "not tenant content".
        /// </summary>
        private static readonly HashSet<string> TenantContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // SharePoint & OneDrive
            "Site", "Web", "List", "ListItem", "Folder", "File", "Drive", "DriveItem", "LoopPage",

            // File kinds. Purview documents the file extension as one of the things this field carries.
            "docx", "xlsx", "pptx", "pdf", "txt", "doc", "xls", "ppt", "csv", "one", "msg",

            // Email & calendar
            "EmailMessage", "Email", "Message", "MailFolder", "Calendar", "Event",

            // Teams
            "Team", "Channel", "Chat", "TeamsMessage", "TeamsMeeting", "TeamsChat", "TeamsChannel",

            // Other Microsoft Graph entities
            "User", "Group", "Contact", "Task", "Planner", "OneNote",
        };

        /// <summary>
        /// Values that describe how the resource was used rather than what it is. <c>CITATION</c> is
        /// undocumented but is one of the most common values Microsoft emits in practice.
        /// </summary>
        private static readonly HashSet<string> UsageRoleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CITATION",
        };

        /// <summary>
        /// Values that mark grounding from outside the tenant. Also undocumented; Microsoft's own
        /// (experimental) ValueLens accelerator maps <c>websearchquery</c> to "Web Searching".
        /// </summary>
        private static readonly HashSet<string> ExternalGroundingTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WebSearchQuery",
        };

        /// <summary>
        /// Host suffixes that only ever serve content belonging to a Microsoft 365 tenant, across the
        /// worldwide and sovereign clouds. Taken from Microsoft's published endpoint lists:
        ///
        /// - Worldwide (and GCC moderate, which uses the worldwide endpoints):
        ///   https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges
        /// - GCC High:
        ///   https://learn.microsoft.com/en-us/microsoft-365/enterprise/microsoft-365-u-s-government-gcc-high-endpoints
        /// - DoD:
        ///   https://learn.microsoft.com/en-us/microsoft-365/enterprise/microsoft-365-u-s-government-dod-endpoints
        /// - Operated by 21Vianet:
        ///   https://learn.microsoft.com/en-us/microsoft-365/enterprise/urls-and-ip-address-ranges-21vianet
        ///
        /// <c>sharepoint.de</c> is included for the retired Microsoft Cloud Deutschland, because
        /// historical audit data can still be imported.
        ///
        /// There is deliberately no <c>onedrive.</c> entry. OneDrive for Business content is served
        /// from the tenant's personal SharePoint host (<c>contoso-my.sharepoint.com</c>), which the
        /// SharePoint suffixes already cover, whereas <c>onedrive.live.com</c> is CONSUMER OneDrive
        /// and is not tenant content at all.
        /// </summary>
        private static readonly string[] TenantContentHostSuffixes =
        {
            // SharePoint Online and OneDrive for Business
            "sharepoint.com", "sharepoint.us", "sharepoint-mil.us", "dps.mil", "sharepoint.cn", "sharepoint.de",

            // Exchange Online / Outlook
            "outlook.office.com", "outlook.office365.com", "outlook.cloud.microsoft",
            "outlook.office365.us", "outlook-dod.office365.us", "outlook.office.de",
            "partner.outlook.cn", "apps.mil",

            // Teams, including the async gateway that serves files shared in chats
            "teams.microsoft.com", "teams.cloud.microsoft", "teams.microsoft.us", "teams.microsoftonline.cn",
        };

        /// <summary>
        /// Hosts that Microsoft operates but which are not tied to one tenant's content, so they are
        /// evidence of NOTHING - neither that a resource belongs to the tenant nor that it came from
        /// outside it.
        ///
        /// This exists because <see cref="IsExternalWebUrl"/> works by exclusion, and
        /// <see cref="TenantContentHostSuffixes"/> is necessarily incomplete: Microsoft can introduce
        /// a content host at any time. Without this, a new Microsoft-operated host would be read as
        /// confident proof of grounding from outside the tenant - reintroducing the under-estimate in
        /// #469 by the opposite route. Being unsure is the safe answer here; claiming external is not.
        ///
        /// <c>microsoft</c> is the brand top-level domain ICANN delegated to Microsoft, so nothing
        /// under it belongs to anyone else. It covers the user-content hosts (<c>usercontent.microsoft</c>,
        /// <c>usgovcloud-usercontent.microsoft</c>) as well as <c>cloud.microsoft</c>.
        /// </summary>
        private static readonly string[] MicrosoftOperatedButNotTenantSpecificSuffixes =
        {
            "microsoft",
            "sovcloud-usercontent.cn",
        };

        /// <summary>
        /// Classifies a raw <c>AccessedResources[].Type</c> value. An unrecognised or missing value is
        /// <see cref="CopilotResourceTypeKind.Unclassified"/>.
        /// </summary>
        public static CopilotResourceTypeKind Classify(string resourceType)
        {
            if (string.IsNullOrWhiteSpace(resourceType)) return CopilotResourceTypeKind.Unclassified;

            var trimmed = resourceType.Trim();

            if (TenantContentTypes.Contains(trimmed)) return CopilotResourceTypeKind.TenantContent;
            if (UsageRoleTypes.Contains(trimmed)) return CopilotResourceTypeKind.UsageRole;
            if (ExternalGroundingTypes.Contains(trimmed)) return CopilotResourceTypeKind.ExternalGrounding;

            return CopilotResourceTypeKind.Unclassified;
        }

        /// <summary>
        /// Whether a <c>SiteUrl</c> points at a host that only serves Microsoft 365 tenant content.
        ///
        /// Matches on the parsed host with a dot boundary rather than on a substring of the whole URL.
        /// A substring test both misses the sovereign clouds and accepts hosts that merely contain the
        /// text, so <c>https://sharepoint.com.example.invalid/x</c> would have counted as tenant
        /// evidence.
        /// </summary>
        public static bool IsTenantContentUrl(string siteUrl)
        {
            var host = WebHostOf(siteUrl);
            if (host == null) return false;

            return MatchesAnySuffix(host, TenantContentHostSuffixes);
        }

        /// <summary>
        /// Whether a <c>SiteUrl</c> positively places the resource OUTSIDE the tenant: it parses as an
        /// absolute web URL, and its host is neither a Microsoft 365 tenant-content host nor any other
        /// host Microsoft operates.
        ///
        /// This is the mirror of <see cref="IsTenantContentUrl"/> and exists so that "the record says
        /// where this lives, and it is not in the tenant" is treated as evidence rather than as not
        /// knowing. A resource with no <c>SiteUrl</c> at all is a different case entirely, and callers
        /// must not conflate the two.
        ///
        /// A Microsoft-operated host that is not tenant-specific returns false here as well as from
        /// <see cref="IsTenantContentUrl"/>: the honest answer for one of those is "cannot tell". See
        /// <see cref="MicrosoftOperatedButNotTenantSpecificSuffixes"/>.
        ///
        /// Known limitation, deliberately accepted: content indexed through a Microsoft Graph
        /// connector is tenant grounding but can carry the source system's own URL, so it reads as
        /// external here. Recognising it would need a signal the audit record does not carry.
        /// </summary>
        public static bool IsExternalWebUrl(string siteUrl)
        {
            var host = WebHostOf(siteUrl);
            if (host == null) return false;

            if (MatchesAnySuffix(host, TenantContentHostSuffixes)) return false;

            return !MatchesAnySuffix(host, MicrosoftOperatedButNotTenantSpecificSuffixes);
        }

        /// <summary>
        /// The comparable host of an absolute http(s) URL, or null when the value is not one.
        ///
        /// Three normalisations matter, all verified against .NET's <see cref="Uri"/>:
        /// the scheme is checked because <c>Uri.Host</c> is populated for <c>file:</c> and <c>ftp:</c>
        /// too, and a non-web URL says nothing about a Microsoft 365 tenant either way;
        /// <c>IdnHost</c> is used because <c>Host</c> preserves alternative Unicode dot separators
        /// (a host written with U+3002 keeps it in <c>Host</c> but canonicalises in <c>IdnHost</c>);
        /// and a trailing DNS root dot is removed because <c>Uri</c> keeps that on both properties.
        /// Without these, a genuine tenant URL fails the suffix match and is read as external.
        /// </summary>
        private static string WebHostOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

            string host;
            try
            {
                host = uri.IdnHost;
            }
            catch (ArgumentException)
            {
                // IdnHost throws on a host it cannot map; the raw host is still worth comparing.
                host = uri.Host;
            }

            if (string.IsNullOrEmpty(host)) return null;

            host = host.TrimEnd('.');
            return host.Length == 0 ? null : host;
        }

        /// <summary>Exact host match, or a match at a dot boundary so a lookalike host cannot pass.</summary>
        private static bool MatchesAnySuffix(string host, string[] suffixes)
        {
            foreach (var suffix in suffixes)
            {
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)) return true;
                if (host.Length > suffix.Length
                    && host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The heading this kind is reported under. Kept here so the Excel workbook and the portal
        /// describe the same buckets with the same words.
        /// </summary>
        public static string KindLabel(CopilotResourceTypeKind kind)
        {
            switch (kind)
            {
                case CopilotResourceTypeKind.TenantContent: return "Tenant content";
                case CopilotResourceTypeKind.UsageRole: return "How it was used";
                case CopilotResourceTypeKind.ExternalGrounding: return "Grounding from outside the tenant";
                default: return "Unclassified";
            }
        }

        /// <summary>
        /// One line explaining why the values under this heading are not comparable with the others.
        /// </summary>
        public static string KindExplanation(CopilotResourceTypeKind kind)
        {
            switch (kind)
            {
                case CopilotResourceTypeKind.TenantContent:
                    return "Kinds of organisational content - file types and Microsoft Graph entities. "
                        + "Undercounted: a file Copilot cited is typed CITATION instead of by its file type.";
                case CopilotResourceTypeKind.UsageRole:
                    return "How the resource was used, not what it is. A citation can be either tenant "
                        + "content or a web page, so these references are counted here and nowhere else.";
                case CopilotResourceTypeKind.ExternalGrounding:
                    return "Grounding from outside the organisation. Not tenant content.";
                default:
                    return "Values this version does not recognise, and references whose type was empty. "
                        + "Microsoft publishes no list of possible values and can add new ones at any time, "
                        + "so these are shown as-is rather than counted as content.";
            }
        }
    }
}

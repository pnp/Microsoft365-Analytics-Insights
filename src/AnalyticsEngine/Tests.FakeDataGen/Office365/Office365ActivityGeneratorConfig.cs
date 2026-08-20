using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Office365
{
    public static class Office365ActivityGeneratorConfig
    {
        public const int SaveBatchSize = 500;
        public const int MaxDocumentPoolSize = 1000;

        public const int SharePointWeight = 40;
        public const int OneDriveWeight = 20;
        public const int ExchangeWeight = 25;
        public const int AzureAdWeight = 15;

        public static readonly string[] SharePointOperations =
        {
            "FileAccessed", "FileModified", "FileDownloaded", "FileUploaded",
            "FileRenamed", "FileMoved", "FileSyncDownloadedFull", "FileSyncUploadedFull",
            "SharingSet", "SharingInvitationCreated"
        };

        public static readonly string[] OneDriveOperations =
        {
            "FileAccessed", "FileModified", "FileDownloaded", "FileUploaded",
            "FileRenamed", "FileMoved", "FileSyncDownloadedFull", "FileSyncUploadedFull",
            "SharingSet"
        };

        public static readonly string[] ExchangeOperations =
        {
            "MailItemsAccessed", "Send", "Create", "Update",
            "MoveToDeletedItems", "SoftDelete", "HardDelete", "New-InboxRule"
        };

        public static readonly string[] AzureAdOperations =
        {
            "UserLoggedIn", "UserLoginFailed", "Add user", "Update user",
            "Add member to group", "Remove member from group",
            "Consent to application", "Change user password"
        };

        public static readonly string[] FileExtensions =
        {
            "docx", "xlsx", "pptx", "pdf", "txt", "one", "csv", "aspx"
        };

        public static readonly string[] FileNameStems =
        {
            "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5",
            "\u03A3\u03C7\u03AD\u03B4\u03B9\u03BF",
            "An\u00E1lisis",
            "\u6226\u7565\u8A08\u753B",
            "Quarterly Report", "Budget Forecast", "Product Roadmap", "Project Proposal",
            "Meeting Notes", "Customer Briefing", "Architecture Review", "Campaign Plan"
        };

        public static readonly string[] FolderNames =
        {
            "Shared Documents", "Projects", "Planning", "Reports",
            "\u039F\u03BC\u03B1\u03B4\u03B9\u03BA\u03AC \u03AD\u03B3\u03B3\u03C1\u03B1\u03C6\u03B1"
        };

        public static readonly Office365SiteDefinition[] SharePointSites =
        {
            new Office365SiteDefinition("https://contoso.sharepoint.com/sites/engineering", "Engineering"),
            new Office365SiteDefinition("https://contoso.sharepoint.com/sites/finance", "Finance"),
            new Office365SiteDefinition("https://contoso.sharepoint.com/sites/hr", "Human Resources"),
            new Office365SiteDefinition("https://contoso.sharepoint.com/sites/marketing", "Marketing"),
            new Office365SiteDefinition("https://contoso.sharepoint.com/sites/projects", "Projects"),
            new Office365SiteDefinition("https://contoso.sharepoint.com/sites/sales", "Sales"),
            new Office365SiteDefinition(
                "https://contoso.sharepoint.com/sites/\u03C3\u03C7\u03AD\u03B4\u03B9\u03B1",
                "Global Planning")
        };

        public static readonly Office365SiteDefinition[] OneDriveSites =
        {
            new Office365SiteDefinition(
                "https://contoso-my.sharepoint.com/personal/alex_contoso_com",
                "Alex OneDrive"),
            new Office365SiteDefinition(
                "https://contoso-my.sharepoint.com/personal/casey_contoso_com",
                "Casey OneDrive"),
            new Office365SiteDefinition(
                "https://contoso-my.sharepoint.com/personal/jordan_contoso_com",
                "Jordan OneDrive"),
            new Office365SiteDefinition(
                "https://contoso-my.sharepoint.com/personal/taylor_contoso_com",
                "Taylor OneDrive")
        };

        public static readonly string[] AuditPropertyNames =
        {
            "ClientIP", "ClientInfoString", "LogonType",
            "ResultStatus", "AuthenticationMethod", "UserAgent"
        };

        public static readonly string[] ExchangeClientIpValues =
        {
            "192.0.2.10", "192.0.2.20", "192.0.2.30", "192.0.2.40"
        };

        public static readonly string[] AzureAdClientIpValues =
        {
            "198.51.100.10", "198.51.100.20", "203.0.113.10", "203.0.113.20"
        };

        public static readonly string[] ExchangeClientInfoValues =
        {
            "OutlookWebApp", "OutlookDesktop", "MobileClient", "ServiceAccount"
        };

        public static readonly string[] LogonTypeValues =
        {
            "Owner", "Delegate", "Admin"
        };

        public static readonly string[] ResultStatusValues =
        {
            "Success", "Failure", "Interrupted"
        };

        public static readonly string[] AuthenticationMethodValues =
        {
            "Password", "MFA", "FIDO2", "Certificate"
        };

        public static readonly string[] UserAgentValues =
        {
            "SyntheticBrowser/1.0", "SyntheticMobile/1.0", "SyntheticOfficeClient/1.0"
        };

        public static string[] GetAllOperationNames()
        {
            return SharePointOperations
                .Concat(OneDriveOperations)
                .Concat(ExchangeOperations)
                .Concat(AzureAdOperations)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string[] GetAllAuditPropertyValues()
        {
            return ExchangeClientIpValues
                .Concat(AzureAdClientIpValues)
                .Concat(ExchangeClientInfoValues)
                .Concat(LogonTypeValues)
                .Concat(ResultStatusValues)
                .Concat(AuthenticationMethodValues)
                .Concat(UserAgentValues)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static IReadOnlyList<Office365SiteDefinition> GetAllSites()
        {
            return SharePointSites
                .Concat(OneDriveSites)
                .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
    }

    public sealed class Office365SiteDefinition
    {
        public string Url { get; }
        public string Title { get; }

        public Office365SiteDefinition(string url, string title)
        {
            Url = url;
            Title = title;
        }
    }
}

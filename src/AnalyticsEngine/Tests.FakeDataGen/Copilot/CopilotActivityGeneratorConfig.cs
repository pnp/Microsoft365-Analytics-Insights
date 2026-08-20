namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Configuration and constants for Copilot activity generation
    /// </summary>
    public static class CopilotActivityGeneratorConfig
    {
        // App host options for Copilot
        // Keep these aligned with the app_host PIVOT in profiling.usp_UpsertCopilot.
        // bizchat is the M365 Chat experience; appchat covers in-app Copilot experiences.
        public static readonly string[] AppHosts = { "Teams", "Word", "Excel", "PowerPoint", "Outlook", "bizchat", "appchat" };

        // Agent names for custom agents
        public static readonly string[] AgentNames = { "Researcher", "Sales Assistant", "HR Helper", "IT Support Bot", "Marketing Agent" };

        // Standard Microsoft agent IDs
        public static readonly string[] StandardAgentIds = { "Microsoft.Copilot.Researcher", "Microsoft.Copilot.Teams", "Microsoft.Copilot.Office" };

        // Department names for user generation
        public static readonly string[] DepartmentNames = { "Engineering", "Sales", "Marketing", "Human Resources", "Finance", "IT", "Customer Support", "Operations", "Product Management", "Legal" };

        // Sample accessed resource data for custom agents
        public static readonly string[] ResourceDocumentNames = { "VacationPolicies.docx", "EmployeeHandbook.pdf", "Q4Report.xlsx", "ProjectProposal.pptx", "Invoices.pdf", "BudgetForecast.xlsx", "ContractTemplate.docx", "SalesPresentation.pptx" };

        public static readonly string[] ResourceSiteUrls = { "https://contoso.sharepoint.com/sites/HR", "https://contoso.sharepoint.com/sites/Finance", "https://contoso.sharepoint.com/sites/Sales", "https://www.accuweather.com/pt/st/sao-tome/295304/february-weather/295304", "https://outlook.office365.com/owa" };

        public static readonly string[] ResourceTypes = { "File", "Email", "WebPage", "ListItem", "Message" };

        // File extensions for document generation
        public static readonly string[] FileExtensions = { "docx", "xlsx", "pptx", "pdf", "txt" };

        // License SKU IDs - https://learn.microsoft.com/en-us/entra/identity/users/licensing-service-plan-reference
        public const string COPILOT_LICENSE_SKU = "Microsoft_365_Copilot";
        public const string E5_LICENSE_SKU = "ENTERPRISEPREMIUM";
        public const string E3_LICENSE_SKU = "ENTERPRISEPACK";
        public const string BUSINESS_PREMIUM_SKU = "SPB";
        public const string EXCHANGE_ONLINE_SKU = "EXCHANGESTANDARD";
    }
}

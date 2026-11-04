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

        /// <summary>
        /// What Copilot did with an accessed resource (audit schema field <c>Action</c>).
        /// </summary>
        public static readonly string[] AccessedResourceActions = { "Read", "Preview", "Reference", "Write" };

        /// <summary>User region on the audit record (<c>ClientRegion</c>).</summary>
        public static readonly string[] ClientRegions = { "US", "GB", "DE", "FR", "JP", "AU", "BR" };

        /// <summary>
        /// Copilot audit-log schema versions (<c>CopilotLogVersion</c>). Several are generated on purpose:
        /// the field exists so payload-shape changes are visible, which is only testable with a mix.
        /// </summary>
        public static readonly string[] CopilotLogVersions = { "1.0", "1.1", "2.0" };

        /// <summary>
        /// Interaction context types (<c>Contexts[].Type</c>) - where the user was when they used Copilot.
        /// </summary>
        public static readonly string[] ContextTypes = { "docx", "xlsx", "pptx", "TeamsMeeting", "TeamsChannel", "TeamsChat", "OneNote" };

        /// <summary>
        /// AI models as (name, provider, version). The dimension de-duplicates on the whole tuple, so the
        /// same model at two versions must produce two rows - generated here so that behaviour is exercised.
        /// </summary>
        public static readonly (string Name, string Provider, string Version)[] AIModels =
        {
            ("gpt-4o", "OpenAI", "2024-08-06"),
            ("gpt-4o", "OpenAI", "2024-11-20"),
            ("gpt-4.1", "OpenAI", "2025-04-14"),
            ("DEEP_LEO", "Microsoft", "1.2"),
            ("phi-4", "Microsoft", "2024-12-12"),
        };

        /// <summary>
        /// AI system plugins as (plugin id, name, version) - the connectors that ground an answer.
        /// Same tuple de-duplication as the models, so a version bump is a new row, not a rewrite.
        /// </summary>
        public static readonly (string PluginId, string Name, string Version)[] AISystemPlugins =
        {
            ("BingWebSearch", "BuiltIn", "1.0"),
            ("SharePointGrounding", "BuiltIn", "2.1"),
            ("SharePointGrounding", "BuiltIn", "2.2"),
            ("GraphConnector.ServiceNow", "Connector", "1.0"),
            ("GraphConnector.Confluence", "Connector", "3.4"),
        };

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

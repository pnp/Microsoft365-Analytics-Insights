namespace Tests.FakeDataGen.Seeding
{
    /// <summary>
    /// Shared seed data used to populate users, lookup tables, and license catalogues.
    /// Centralised so every data generator and stress test pre-loads the same metadata.
    /// </summary>
    public static class SeedDataCatalogue
    {
        public static readonly string[] Departments =
        {
            "Engineering", "Marketing", "Sales", "Finance", "Human Resources", "Legal",
            "Operations", "Product", "Design", "Customer Support", "IT", "Research & Development"
        };

        public static readonly string[] Companies =
        {
            "Contoso Ltd", "Fabrikam Inc", "Northwind Traders", "Adventure Works",
            "Woodgrove Bank", "Tailspin Toys", "Litware Inc", "Proseware"
        };

        public static readonly string[] JobTitles =
        {
            "Software Engineer", "Senior Developer", "Product Manager", "Data Analyst",
            "UX Designer", "DevOps Engineer", "Solutions Architect", "Business Analyst",
            "Project Manager", "QA Engineer", "Technical Lead", "VP of Engineering",
            "Marketing Specialist", "Account Executive", "Support Engineer"
        };

        public static readonly string[] StatesOrProvinces =
        {
            "Washington", "California", "Texas", "New York", "Massachusetts",
            "Greater London", "Île-de-France", "Bavaria", "Catalonia", "New South Wales"
        };

        public static readonly string[] Countries =
        {
            "United States", "United Kingdom", "Germany", "France", "Spain",
            "Australia", "Canada", "Japan", "Ireland", "Netherlands"
        };

        public static readonly string[] OfficeLocations =
        {
            "Redmond/B33", "Redmond/B25", "London/PV", "Munich/MU1", "Paris/IS1",
            "Dublin/D2", "Sydney/SY1", "Tokyo/TK1", "Madrid/MA1", "Toronto/TO1"
        };

        public static readonly string[] UsageLocations =
        {
            "US", "GB", "DE", "FR", "ES", "AU", "CA", "JP", "IE", "NL"
        };

        /// <summary>
        /// License catalogue used by data generators and stress tests. SKU IDs reference:
        /// https://learn.microsoft.com/entra/identity/users/licensing-service-plan-reference
        /// </summary>
        public static readonly (string Name, string SkuId)[] LicenseCatalogue =
        {
            ("Microsoft 365 Copilot",          "Microsoft_365_Copilot"),
            ("Office 365 E5",                  "ENTERPRISEPREMIUM"),
            ("Office 365 E3",                  "ENTERPRISEPACK"),
            ("Microsoft 365 Business Premium", "SPB"),
            ("Exchange Online Plan 1",         "EXCHANGESTANDARD"),
            ("Power BI Pro",                   "POWER_BI_PRO"),
            ("Power Automate Premium",         "POWERAUTOMATE_ATTENDED_RPA")
        };
    }
}

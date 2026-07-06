using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Seeding
{
    /// <summary>
    /// Shared seed data used to populate users, lookup tables, and license catalogues.
    /// Centralised so every data generator and stress test pre-loads the same metadata.
    ///
    /// The goal is data that is as realistic as a live tenant: users span many countries,
    /// their geography is internally consistent (country / state / city / office / usage
    /// location / postal code all agree), job titles fit their department, they live on a
    /// handful of different email domains and a realistic minority have disabled accounts.
    /// Some values are deliberately non-Latin (Greek "Αττική", Japanese "東京都", accented
    /// "São Paulo" / "Zürich") so Unicode round-trips are exercised by the fake data too.
    /// </summary>
    public static class SeedDataCatalogue
    {
        /// <summary>Fraction of seeded users whose account is disabled (leavers / suspended).</summary>
        public const double DisabledAccountFraction = 0.07;

        // ---------------------------------------------------------------------
        // Email domains
        // ---------------------------------------------------------------------

        /// <summary>
        /// A handful of tenant domains users are spread across (multi-domain tenants are the
        /// norm after acquisitions / rebrands). Assignment is deterministic by user index via
        /// <see cref="DomainForIndex"/> so a seeder and any code that rebuilds the same UPNs
        /// (e.g. an event catalogue that must join back to the seeded users) always agree.
        /// </summary>
        public static readonly string[] Domains =
        {
            "contoso.com", "fabrikam.com", "northwindtraders.com",
            "adventure-works.com", "woodgrovebank.com", "tailspintoys.com"
        };

        /// <summary>Deterministic domain for a user index (stable across processes / reruns).</summary>
        public static string DomainForIndex(int index)
        {
            int i = ((index % Domains.Length) + Domains.Length) % Domains.Length;
            return Domains[i];
        }

        /// <summary>
        /// Builds a user principal name. When <paramref name="domainOverride"/> is null the
        /// domain is chosen deterministically from <see cref="Domains"/> by index, giving a
        /// realistic multi-domain spread that every caller reproduces identically.
        /// </summary>
        public static string BuildUpn(string prefix, int index, string domainOverride = null)
        {
            return $"{prefix}{index}@{domainOverride ?? DomainForIndex(index)}";
        }

        // ---------------------------------------------------------------------
        // Geography - each locale is internally consistent
        // ---------------------------------------------------------------------

        /// <summary>
        /// A single, coherent place a user can belong to. Assigning a whole locale at once
        /// keeps country / state / city / office / usage location / postal code consistent,
        /// instead of picking each column independently (which produced impossible people
        /// such as "United States / Bavaria / Tokyo / ES").
        /// </summary>
        public sealed class GeoLocale
        {
            public string Country { get; }
            public string StateOrProvince { get; }
            public string City { get; }
            public string OfficeLocation { get; }
            public string UsageLocation { get; }
            private readonly Func<Random, string> _postalCode;

            public GeoLocale(string country, string stateOrProvince, string city,
                string officeLocation, string usageLocation, Func<Random, string> postalCode)
            {
                Country = country;
                StateOrProvince = stateOrProvince;
                City = city;
                OfficeLocation = officeLocation;
                UsageLocation = usageLocation;
                _postalCode = postalCode;
            }

            /// <summary>A freshly-formatted, country-appropriate postal code (empty where a country has none).</summary>
            public string NewPostalCode(Random random) => _postalCode?.Invoke(random) ?? string.Empty;
        }

        /// <summary>
        /// Worldwide set of coherent locales (21 countries across every populated continent).
        /// Multiple offices in the same country are separate entries so city / office / postal
        /// code stay consistent with each other.
        /// </summary>
        public static readonly GeoLocale[] Locales =
        {
            // United States
            new GeoLocale("United States", "Washington", "Redmond", "Redmond/B33", "US", r => "980" + D(r, 2)),
            new GeoLocale("United States", "Washington", "Redmond", "Redmond/B25", "US", r => "980" + D(r, 2)),
            new GeoLocale("United States", "California", "Mountain View", "MountainView/MV1", "US", r => "9403" + D(r, 1)),
            new GeoLocale("United States", "New York", "New York", "NewYork/NY1", "US", r => "100" + D(r, 2)),
            new GeoLocale("United States", "Texas", "Austin", "Austin/AU1", "US", r => "787" + D(r, 2)),
            new GeoLocale("United States", "Massachusetts", "Cambridge", "Cambridge/CB1", "US", r => "021" + D(r, 2)),
            // United Kingdom
            new GeoLocale("United Kingdom", "Greater London", "London", "London/PV", "GB", r => $"SW{D(r, 1)}{L(r)} {D(r, 1)}{L(r)}{L(r)}"),
            new GeoLocale("United Kingdom", "Scotland", "Edinburgh", "Edinburgh/ED1", "GB", r => $"EH{D(r, 1)} {D(r, 1)}{L(r)}{L(r)}"),
            // Ireland
            new GeoLocale("Ireland", "Leinster", "Dublin", "Dublin/D2", "IE", r => $"D{D(r, 2)} {L(r)}{D(r, 1)}{L(r)}{D(r, 1)}"),
            // Germany
            new GeoLocale("Germany", "Bavaria", "Munich", "Munich/MU1", "DE", r => "80" + D(r, 3)),
            new GeoLocale("Germany", "Berlin", "Berlin", "Berlin/BE1", "DE", r => "10" + D(r, 3)),
            // France
            new GeoLocale("France", "Île-de-France", "Paris", "Paris/IS1", "FR", r => "750" + D(r, 2)),
            // Spain
            new GeoLocale("Spain", "Community of Madrid", "Madrid", "Madrid/MA1", "ES", r => "280" + D(r, 2)),
            new GeoLocale("Spain", "Catalonia", "Barcelona", "Barcelona/BA1", "ES", r => "080" + D(r, 2)),
            // Netherlands
            new GeoLocale("Netherlands", "North Holland", "Amsterdam", "Amsterdam/AM1", "NL", r => $"10{D(r, 2)} {L(r)}{L(r)}"),
            // Italy
            new GeoLocale("Italy", "Lombardy", "Milan", "Milan/MI1", "IT", r => "201" + D(r, 2)),
            // Switzerland
            new GeoLocale("Switzerland", "Zürich", "Zürich", "Zurich/ZH1", "CH", r => "80" + D(r, 2)),
            // Sweden
            new GeoLocale("Sweden", "Stockholm", "Stockholm", "Stockholm/ST1", "SE", r => $"1{D(r, 2)} {D(r, 2)}"),
            // Poland
            new GeoLocale("Poland", "Masovia", "Warsaw", "Warsaw/WA1", "PL", r => $"00-{D(r, 3)}"),
            // Greece (Unicode state)
            new GeoLocale("Greece", "Αττική", "Athens", "Athens/AT1", "GR", r => $"1{D(r, 2)} {D(r, 2)}"),
            // Canada
            new GeoLocale("Canada", "Ontario", "Toronto", "Toronto/TO1", "CA", r => $"M{D(r, 1)}{L(r)} {D(r, 1)}{L(r)}{D(r, 1)}"),
            new GeoLocale("Canada", "British Columbia", "Vancouver", "Vancouver/VA1", "CA", r => $"V{D(r, 1)}{L(r)} {D(r, 1)}{L(r)}{D(r, 1)}"),
            // Brazil (accented city / state)
            new GeoLocale("Brazil", "São Paulo", "São Paulo", "SaoPaulo/SP1", "BR", r => $"0{D(r, 4)}-{D(r, 3)}"),
            // Mexico (accented state)
            new GeoLocale("Mexico", "Ciudad de México", "Mexico City", "MexicoCity/MX1", "MX", r => "0" + D(r, 4)),
            // India
            new GeoLocale("India", "Karnataka", "Bengaluru", "Bengaluru/BG1", "IN", r => "5600" + D(r, 2)),
            new GeoLocale("India", "Maharashtra", "Mumbai", "Mumbai/MB1", "IN", r => "4000" + D(r, 2)),
            // Japan (Unicode prefecture)
            new GeoLocale("Japan", "東京都", "Tokyo", "Tokyo/TK1", "JP", r => $"1{D(r, 2)}-{D(r, 4)}"),
            // Australia
            new GeoLocale("Australia", "New South Wales", "Sydney", "Sydney/SY1", "AU", r => "20" + D(r, 2)),
            new GeoLocale("Australia", "Victoria", "Melbourne", "Melbourne/ME1", "AU", r => "30" + D(r, 2)),
            // Singapore
            new GeoLocale("Singapore", "Central Region", "Singapore", "Singapore/SG1", "SG", r => D(r, 6)),
            // United Arab Emirates (no postal code system)
            new GeoLocale("United Arab Emirates", "Dubai", "Dubai", "Dubai/DU1", "AE", r => string.Empty),
            // South Africa
            new GeoLocale("South Africa", "Gauteng", "Johannesburg", "Johannesburg/JO1", "ZA", r => D(r, 4)),
        };

        // ---------------------------------------------------------------------
        // Departments -> plausible job titles (a title always fits its department)
        // ---------------------------------------------------------------------

        public static readonly (string Department, string[] Titles)[] DepartmentJobTitles =
        {
            ("Engineering", new[] { "Software Engineer", "Senior Software Engineer", "Principal Engineer", "Engineering Manager", "DevOps Engineer", "Site Reliability Engineer", "QA Engineer", "Technical Lead" }),
            ("Product", new[] { "Product Manager", "Senior Product Manager", "Group Product Manager", "Product Owner", "Director of Product" }),
            ("Design", new[] { "UX Designer", "UX Researcher", "Product Designer", "Design Lead" }),
            ("Sales", new[] { "Account Executive", "Sales Development Representative", "Sales Manager", "Regional Sales Director", "Solutions Engineer" }),
            ("Marketing", new[] { "Marketing Specialist", "Content Marketing Manager", "Product Marketing Manager", "SEO Analyst", "Chief Marketing Officer" }),
            ("Finance", new[] { "Financial Analyst", "Accountant", "Finance Manager", "Controller", "Chief Financial Officer" }),
            ("Human Resources", new[] { "HR Generalist", "Technical Recruiter", "People Operations Manager", "HR Business Partner" }),
            ("Legal", new[] { "Corporate Counsel", "Paralegal", "Compliance Manager", "General Counsel" }),
            ("Operations", new[] { "Operations Analyst", "Operations Manager", "Program Manager", "Chief Operating Officer" }),
            ("Customer Support", new[] { "Support Engineer", "Customer Success Manager", "Support Team Lead", "Technical Account Manager" }),
            ("IT", new[] { "IT Support Specialist", "Systems Administrator", "Network Engineer", "IT Manager", "Chief Information Security Officer" }),
            ("Research & Development", new[] { "Research Scientist", "Applied Scientist", "Data Scientist", "Data Analyst", "R&D Lead" }),
            ("Executive", new[] { "Chief Executive Officer", "Chief Technology Officer", "VP of Engineering", "VP of Sales", "Executive Assistant" }),
        };

        public static readonly string[] Companies =
        {
            "Contoso Ltd", "Fabrikam Inc", "Northwind Traders", "Adventure Works",
            "Woodgrove Bank", "Tailspin Toys", "Litware Inc", "Proseware Inc",
            "Wingtip Toys", "Alpine Ski House", "Trey Research", "Margie's Travel",
            "Graphic Design Institute", "Lucerne Publishing"
        };

        // Derived lookup arrays (distinct, insertion order preserved) so existing consumers
        // that expect a flat string[] keep working while the data stays coherent.
        public static readonly string[] Departments;
        public static readonly string[] JobTitles;
        public static readonly string[] Countries;
        public static readonly string[] StatesOrProvinces;
        public static readonly string[] OfficeLocations;
        public static readonly string[] UsageLocations;

        static SeedDataCatalogue()
        {
            Departments = DepartmentJobTitles.Select(d => d.Department).ToArray();
            JobTitles = DistinctInOrder(DepartmentJobTitles.SelectMany(d => d.Titles));
            Countries = DistinctInOrder(Locales.Select(l => l.Country));
            StatesOrProvinces = DistinctInOrder(Locales.Select(l => l.StateOrProvince));
            OfficeLocations = DistinctInOrder(Locales.Select(l => l.OfficeLocation));
            UsageLocations = DistinctInOrder(Locales.Select(l => l.UsageLocation));
        }

        /// <summary>
        /// License catalogue used by data generators and stress tests. SKU IDs reference:
        /// https://learn.microsoft.com/entra/identity/users/licensing-service-plan-reference
        /// </summary>
        public static readonly (string Name, string SkuId)[] LicenseCatalogue =
        {
            ("Microsoft 365 Copilot",          "Microsoft_365_Copilot"),
            ("Office 365 E5",                  "ENTERPRISEPREMIUM"),
            ("Office 365 E3",                  "ENTERPRISEPACK"),
            ("Office 365 E1",                  "STANDARDPACK"),
            ("Microsoft 365 Business Premium", "SPB"),
            ("Microsoft 365 F3",               "SPE_F1"),
            ("Exchange Online Plan 1",         "EXCHANGESTANDARD"),
            ("Power BI Pro",                   "POWER_BI_PRO"),
            ("Power Automate Premium",         "POWERAUTOMATE_ATTENDED_RPA"),
            ("Visio Plan 2",                   "VISIOCLIENT"),
            ("Project Plan 3",                 "PROJECTPROFESSIONAL")
        };

        // ---------------------------------------------------------------------
        // Coherent per-user profile
        // ---------------------------------------------------------------------

        /// <summary>A single, internally-consistent, realistic set of user metadata.</summary>
        public sealed class UserProfile
        {
            public string Country { get; set; }
            public string StateOrProvince { get; set; }
            public string City { get; set; }
            public string OfficeLocation { get; set; }
            public string UsageLocation { get; set; }
            public string PostalCode { get; set; }
            public string Department { get; set; }
            public string JobTitle { get; set; }
            public string Company { get; set; }
            public bool AccountEnabled { get; set; }
        }

        /// <summary>
        /// Produces one coherent, realistic profile: a whole geo locale, a department with a
        /// job title that fits it, a company, a country-appropriate postal code and a realistic
        /// account-enabled state (a small minority are disabled).
        /// </summary>
        public static UserProfile NextUserProfile(Random random)
        {
            var locale = Locales[random.Next(Locales.Length)];
            var dept = DepartmentJobTitles[random.Next(DepartmentJobTitles.Length)];
            return new UserProfile
            {
                Country = locale.Country,
                StateOrProvince = locale.StateOrProvince,
                City = locale.City,
                OfficeLocation = locale.OfficeLocation,
                UsageLocation = locale.UsageLocation,
                PostalCode = locale.NewPostalCode(random),
                Department = dept.Department,
                JobTitle = dept.Titles[random.Next(dept.Titles.Length)],
                Company = Companies[random.Next(Companies.Length)],
                AccountEnabled = random.NextDouble() >= DisabledAccountFraction
            };
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static string[] DistinctInOrder(IEnumerable<string> values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            foreach (var value in values)
            {
                if (value != null && seen.Add(value)) ordered.Add(value);
            }
            return ordered.ToArray();
        }

        /// <summary>Returns <paramref name="n"/> random decimal digits as a string.</summary>
        private static string D(Random random, int n)
        {
            var chars = new char[n];
            for (int i = 0; i < n; i++) chars[i] = (char)('0' + random.Next(10));
            return new string(chars);
        }

        /// <summary>Returns a random uppercase A-Z letter (for UK / Canada / NL style postal codes).</summary>
        private static char L(Random random) => (char)('A' + random.Next(26));
    }
}

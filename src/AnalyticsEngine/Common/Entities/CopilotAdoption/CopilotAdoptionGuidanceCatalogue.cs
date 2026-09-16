using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Versioned Microsoft guidance links attached to the action the product recommends.
    /// </summary>
    public static class CopilotAdoptionGuidanceCatalogue
    {
        public const string Version = "2026.09.16";

        public const string UnlicensedActionCode = "unlicensed";

        private static readonly IReadOnlyList<AdoptionGuidanceLink> Links = new[]
        {
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, "License allocation guidance", "https://aka.ms/Copilot/LicenseAllocationGuide", "Microsoft 365 Copilot license allocation guidance for rapid value", "admin"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, "Copilot Academy", "https://aka.ms/copilot-academy", "Viva Learning", "admin"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Reengage, "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "departmentLead"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Coach, "Copilot Academy", "https://aka.ms/copilot-academy", "Viva Learning", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Coach, "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "enablementOwner"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Broaden, "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "departmentLead"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Grow, "Copilot Success Kit", "https://aka.ms/Copilot/SuccessKit", "Copilot Success Kit – Microsoft Adoption", "departmentLead"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Grow, "Microsoft Scenario Library", "https://aka.ms/ScenarioLibrary", "Microsoft Scenario Library – Microsoft Adoption", "departmentLead"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Advocate, "Microsoft 365 Copilot Adoption Playbook", "https://www.microsoft.com/en-us/microsoft-365-copilot/copilot-adoption-guide", "Microsoft 365 Copilot Adoption Playbook | Microsoft Copilot", "enablementOwner"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Advocate, "Creating an AI Council", "https://adoption.microsoft.com/en-us/copilot/ai-council/", "Creating an AI Council – Microsoft Adoption", "enablementOwner"),

            Link(CopilotAdoptionScoring.AdoptionActionCodes.Review, "License allocation guidance", "https://aka.ms/Copilot/LicenseAllocationGuide", "Microsoft 365 Copilot license allocation guidance for rapid value", "admin"),
            Link(CopilotAdoptionScoring.AdoptionActionCodes.Excluded, "License allocation guidance", "https://aka.ms/Copilot/LicenseAllocationGuide", "Microsoft 365 Copilot license allocation guidance for rapid value", "admin"),

            Link(UnlicensedActionCode, "Microsoft Copilot Readiness Report", "https://learn.microsoft.com/en-us/microsoft-365/admin/activity-reports/microsoft-365-copilot-readiness", "Microsoft Copilot Readiness Report - Microsoft 365 admin | Microsoft Learn", "budgetOwner"),
            Link(UnlicensedActionCode, "Implementation Summary Guide for Leaders", "https://aka.ms/Copilot/ImplementationSummaryGuide", "Microsoft 365 Copilot Implementation executive summary", "budgetOwner"),
        };

        public static IReadOnlyList<AdoptionGuidanceLink> All => Links.Select(Clone).ToList();

        public static IReadOnlyList<AdoptionGuidanceLink> ForAction(string actionCode)
        {
            if (string.IsNullOrWhiteSpace(actionCode)) return new List<AdoptionGuidanceLink>();

            return Links
                .Where(l => string.Equals(l.ActionCode, actionCode, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .ToList();
        }

        public static string TitlesForAction(string actionCode)
        {
            return string.Join(" | ", ForAction(actionCode).Select(l => l.Title));
        }

        public static string UrlsForAction(string actionCode)
        {
            return string.Join(" | ", ForAction(actionCode).Select(l => l.Url));
        }

        private static AdoptionGuidanceLink Link(
            string actionCode,
            string title,
            string url,
            string expectedTitle,
            string audience)
        {
            return new AdoptionGuidanceLink
            {
                ActionCode = actionCode,
                Title = title,
                Url = url,
                ExpectedTitle = expectedTitle,
                Audience = audience,
                Publisher = "Microsoft",
                CatalogueVersion = Version,
            };
        }

        private static AdoptionGuidanceLink Clone(AdoptionGuidanceLink link)
        {
            return new AdoptionGuidanceLink
            {
                ActionCode = link.ActionCode,
                Title = link.Title,
                Url = link.Url,
                ExpectedTitle = link.ExpectedTitle,
                Audience = link.Audience,
                Publisher = link.Publisher,
                CatalogueVersion = link.CatalogueVersion,
            };
        }
    }
}
